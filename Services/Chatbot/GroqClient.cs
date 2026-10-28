using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using project_approval_system.Services.Chatbot.Models;

namespace project_approval_system.Services.Chatbot;

internal sealed class GroqClient
{
    private const string KeyConfigPath = "Groq:ApiKey";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly GroqOptions _options;
    private readonly string? _apiKey;

    public GroqClient(HttpClient http, IOptions<GroqOptions> options, IConfiguration config)
    {
        _http = http;
        _options = options.Value;
        var rawKey = config[KeyConfigPath] ?? config["GROQ_API_KEY"];
        _apiKey = string.IsNullOrWhiteSpace(rawKey) ? null : rawKey.Trim();
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public async IAsyncEnumerable<GroqStreamItem> StreamAsync(
        GroqChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new InvalidOperationException(
                $"Groq API key missing. Set it via user secrets: dotnet user-secrets set \"{KeyConfigPath}\" \"gsk_...\"");
        }

        request.Stream = true;

        var endpoint = $"{_options.BaseUrl.TrimEnd('/')}/v1/chat/completions";
        using var http = new HttpRequestMessage(HttpMethod.Post, endpoint);
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var json = JsonSerializer.Serialize(request, SerializerOptions);
        http.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Groq API error {(int)response.StatusCode}: {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var textBuffer = new StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallBuilder>();
        string? finishReason = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || line[0] == ':')
            {
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == "[DONE]")
            {
                break;
            }

            foreach (var item in ProcessChunk(payload, textBuffer, toolCalls, ref finishReason))
            {
                yield return item;
            }
        }

        var blocks = new List<ChatContentBlock>();
        if (textBuffer.Length > 0)
        {
            blocks.Add(new ChatContentBlock
            {
                Type = ChatContentType.Text,
                Text = textBuffer.ToString(),
            });
        }

        foreach (var (index, builder) in toolCalls.OrderBy(kvp => kvp.Key))
        {
            var id = string.IsNullOrEmpty(builder.Id) ? $"call_{index}" : builder.Id;
            if (!builder.StartedEmitted && !string.IsNullOrEmpty(builder.Name))
            {
                builder.StartedEmitted = true;
                yield return new GroqStreamItem.ToolUseStarted(id, builder.Name);
            }
            blocks.Add(builder.Build(id));
        }

        var effectiveStop = finishReason ?? (toolCalls.Count > 0 ? "tool_calls" : "stop");
        yield return new GroqStreamItem.StopReason(effectiveStop);
        yield return new GroqStreamItem.MessageBuilt(blocks);
    }

    private static IEnumerable<GroqStreamItem> ProcessChunk(
        string payload,
        StringBuilder textBuffer,
        Dictionary<int, ToolCallBuilder> toolCalls,
        ref string? finishReason)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorElement))
        {
            var msg = errorElement.TryGetProperty("message", out var errMsg)
                ? errMsg.GetString()
                : errorElement.GetRawText();
            throw new InvalidOperationException($"Groq stream error: {msg ?? "Unknown API error"}");
        }

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return Array.Empty<GroqStreamItem>();
        }

        var items = new List<GroqStreamItem>();
        var choice = choices[0];

        if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
        {
            finishReason = fr.GetString();
        }

        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return items;
        }

        if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
        {
            var text = contentEl.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                textBuffer.Append(text);
                items.Add(new GroqStreamItem.TextDelta(text));
            }
        }

        if (delta.TryGetProperty("tool_calls", out var tcArray) && tcArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcArray.EnumerateArray())
            {
                var index = tc.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var idx) ? idx : 0;
                if (!toolCalls.TryGetValue(index, out var builder))
                {
                    builder = new ToolCallBuilder();
                    toolCalls[index] = builder;
                }

                if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrEmpty(id))
                    {
                        builder.Id = id;
                    }
                }

                if (tc.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object)
                {
                    if (fnEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        builder.Name += nameEl.GetString();
                    }

                    if (fnEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                    {
                        builder.ArgumentsBuffer.Append(argsEl.GetString());
                    }
                }

                if (!builder.StartedEmitted && !string.IsNullOrEmpty(builder.Id) && !string.IsNullOrEmpty(builder.Name))
                {
                    builder.StartedEmitted = true;
                    items.Add(new GroqStreamItem.ToolUseStarted(builder.Id, builder.Name));
                }
            }
        }

        return items;
    }

    private sealed class ToolCallBuilder
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder ArgumentsBuffer { get; } = new();
        public bool StartedEmitted { get; set; }

        public ChatContentBlock Build(string id)
        {
            var json = ArgumentsBuffer.Length == 0 ? "{}" : ArgumentsBuffer.ToString();
            JsonElement input;
            try
            {
                input = JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch (JsonException)
            {
                input = JsonSerializer.Deserialize<JsonElement>("{}");
            }

            return new ChatContentBlock
            {
                Type = ChatContentType.ToolUse,
                ToolUseId = id,
                ToolName = Name,
                ToolInput = input,
            };
        }
    }
}

internal abstract record GroqStreamItem
{
    public sealed record TextDelta(string Text) : GroqStreamItem;
    public sealed record ToolUseStarted(string Id, string Name) : GroqStreamItem;
    public sealed record StopReason(string Reason) : GroqStreamItem;
    public sealed record MessageBuilt(List<ChatContentBlock> Blocks) : GroqStreamItem;
}
