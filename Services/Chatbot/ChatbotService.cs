using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using project_approval_system.Data;
using project_approval_system.Services.Chatbot.Models;

namespace project_approval_system.Services.Chatbot;

internal sealed class ChatbotService(
    GroqClient client,
    IChatToolRegistry toolRegistry,
    IPendingActionStore pendingActionStore,
    IOptions<GroqOptions> options,
    IWebHostEnvironment env,
    ILogger<ChatbotService> logger) : IChatbotService
{
    private static readonly HashSet<string> WriteToolNames = new(StringComparer.Ordinal)
    {
        "withdraw_my_proposal",
        "express_interest",
        "confirm_match",
        "admin_assign",
    };

    private readonly GroqOptions _options = options.Value;
    private string? _cachedSystemPromptTemplate;

    public async IAsyncEnumerable<ChatTurnEvent> RunTurnAsync(
        IList<ChatMessage> history,
        string userInput,
        ClaimsPrincipal user,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ctx = BuildContext(user);
        if (ctx is null)
        {
            yield return new TurnErrorEvent("You must be signed in with a recognised role to use the assistant.");
            yield break;
        }

        history.Add(ChatMessage.UserText(userInput));

        await foreach (var ev in ContinueLoopAsync(history, user, ctx.Value, ct))
        {
            yield return ev;
        }
    }

    public async IAsyncEnumerable<ChatTurnEvent> ResumePendingActionAsync(
        IList<ChatMessage> history,
        string token,
        bool confirmed,
        ClaimsPrincipal user,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ctx = BuildContext(user);
        if (ctx is null)
        {
            yield return new TurnErrorEvent("You must be signed in to resume an action.");
            yield break;
        }

        var pending = pendingActionStore.Consume(token, ctx.Value.UserId);
        if (pending is null)
        {
            yield return new TurnErrorEvent("This confirmation has expired or is no longer valid.");
            yield break;
        }

        var placeholder = FindPlaceholder(history, pending.ToolUseId);
        if (placeholder is null)
        {
            yield return new TurnErrorEvent("The pending action is no longer in the conversation history.");
            yield break;
        }

        string resultJson;
        var isError = false;
        bool succeeded;
        string message;

        if (!confirmed)
        {
            resultJson = JsonSerializer.Serialize(new
            {
                status = "user_canceled",
                message = "The user declined to execute this action.",
            });
            succeeded = false;
            message = "Canceled.";
        }
        else
        {
            try
            {
                var args = JsonSerializer.Deserialize<JsonElement>(pending.ArgsJson);
                resultJson = await toolRegistry.ExecuteAsync(pending.ToolName, args, ctx.Value, ct);
                succeeded = true;
                message = "Executed.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Write tool '{Tool}' failed", pending.ToolName);
                resultJson = JsonSerializer.Serialize(new { error = ex.Message });
                isError = true;
                succeeded = false;
                message = ex.Message;
            }
        }

        placeholder.ToolResultContent = resultJson;
        placeholder.ToolResultIsError = isError;

        yield return new ToolCallFinishedEvent(pending.ToolUseId, pending.ToolName, resultJson.Length, isError);
        yield return new ActionCommittedEvent(token, confirmed, succeeded, message);

        await foreach (var ev in ContinueLoopAsync(history, user, ctx.Value, ct))
        {
            yield return ev;
        }
    }

    private async IAsyncEnumerable<ChatTurnEvent> ContinueLoopAsync(
        IList<ChatMessage> history,
        ClaimsPrincipal user,
        ChatToolContext ctx,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!client.IsConfigured)
        {
            yield return new TurnErrorEvent("Assistant API key is not configured.");
            yield break;
        }

        var toolDefs = toolRegistry.ForUser(user);
        var systemPrompt = BuildSystemPrompt(ctx);

        var groqTools = toolDefs.Select(t => new GroqTool
        {
            Type = "function",
            Function = new GroqFunctionDefinition
            {
                Name = t.Name,
                Description = t.Description,
                Parameters = t.InputSchema,
            },
        }).ToList();

        for (var iteration = 0; iteration < _options.MaxToolIterations; iteration++)
        {
            var request = new GroqChatRequest
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                Messages = ToGroqMessages(systemPrompt, history),
                Tools = groqTools.Count > 0 ? groqTools : null,
            };

            List<ChatContentBlock>? assistantBlocks = null;
            string? stopReason = null;
            Exception? streamError = null;

            var stream = client.StreamAsync(request, ct);
            var enumerator = stream.GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    GroqStreamItem? item = null;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }
                        item = enumerator.Current;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        streamError = ex;
                        break;
                    }

                    switch (item)
                    {
                        case GroqStreamItem.TextDelta td:
                            yield return new TextDeltaEvent(td.Text);
                            break;
                        case GroqStreamItem.ToolUseStarted tus:
                            yield return new ToolCallStartedEvent(tus.Id, tus.Name);
                            break;
                        case GroqStreamItem.StopReason sr:
                            stopReason = sr.Reason;
                            break;
                        case GroqStreamItem.MessageBuilt mb:
                            assistantBlocks = mb.Blocks;
                            break;
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (streamError is not null)
            {
                logger.LogError(streamError, "Chatbot stream failed");
                yield return new TurnErrorEvent("The assistant couldn't reach the model. Please try again.");
                yield break;
            }

            if (assistantBlocks is null || assistantBlocks.Count == 0)
            {
                yield return new TurnErrorEvent("The assistant returned no content.");
                yield break;
            }

            var assistantMessage = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = assistantBlocks,
            };
            history.Add(assistantMessage);
            yield return new AssistantMessageCompletedEvent(assistantMessage);

            var toolUseBlocks = assistantBlocks.Where(b => b.Type == ChatContentType.ToolUse).ToList();
            if (toolUseBlocks.Count == 0)
            {
                yield return new TurnCompletedEvent(stopReason ?? "stop");
                yield break;
            }

            var toolResults = new List<ChatContentBlock>();
            var writeGated = false;
            foreach (var call in toolUseBlocks)
            {
                var toolCallId = call.ToolUseId ?? "";
                var toolName = call.ToolName ?? "";
                var toolInput = call.ToolInput ?? JsonDocument.Parse("{}").RootElement;

                if (WriteToolNames.Contains(toolName))
                {
                    var argsJson = toolInput.GetRawText();
                    var token = pendingActionStore.Enqueue(ctx.UserId, toolCallId, toolName, argsJson);
                    var summary = SummarizeWriteAction(toolName, toolInput);
                    var placeholderJson = JsonSerializer.Serialize(new
                    {
                        status = "awaiting_user_confirmation",
                        token,
                    });
                    toolResults.Add(new ChatContentBlock
                    {
                        Type = ChatContentType.ToolResult,
                        ToolUseId = toolCallId,
                        ToolResultContent = placeholderJson,
                        ToolResultIsError = false,
                    });
                    writeGated = true;
                    yield return new ActionConfirmationRequiredEvent(token, toolCallId, toolName, summary, argsJson);
                }
                else
                {
                    string resultJson;
                    bool isError;
                    try
                    {
                        resultJson = await toolRegistry.ExecuteAsync(toolName, toolInput, ctx, ct);
                        isError = false;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Tool '{Tool}' failed", toolName);
                        resultJson = JsonSerializer.Serialize(new { error = ex.Message });
                        isError = true;
                    }

                    toolResults.Add(new ChatContentBlock
                    {
                        Type = ChatContentType.ToolResult,
                        ToolUseId = toolCallId,
                        ToolResultContent = resultJson,
                        ToolResultIsError = isError,
                    });

                    yield return new ToolCallFinishedEvent(toolCallId, toolName, resultJson.Length, isError);
                }
            }

            history.Add(new ChatMessage { Role = ChatRole.User, Content = toolResults });

            if (writeGated)
            {
                yield return new TurnCompletedEvent("awaiting_user_confirmation");
                yield break;
            }
        }

        yield return new TurnErrorEvent($"Tool-use loop exceeded the {_options.MaxToolIterations}-iteration limit.");
    }

    private static ChatContentBlock? FindPlaceholder(IList<ChatMessage> history, string toolUseId)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role != ChatRole.User) continue;
            foreach (var block in msg.Content)
            {
                if (block.Type == ChatContentType.ToolResult && block.ToolUseId == toolUseId)
                {
                    return block;
                }
            }
        }
        return null;
    }

    private static string SummarizeWriteAction(string toolName, JsonElement args)
    {
        int? proposalId = args.ValueKind == JsonValueKind.Object
                          && args.TryGetProperty("proposalId", out var p)
                          && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32()
            : null;
        string? supervisorId = args.ValueKind == JsonValueKind.Object
                               && args.TryGetProperty("supervisorId", out var s)
                               && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;

        return toolName switch
        {
            "withdraw_my_proposal" => proposalId is null
                ? "Withdraw a proposal"
                : $"Withdraw proposal #{proposalId}",
            "express_interest" => proposalId is null
                ? "Express interest in a proposal"
                : $"Express interest in proposal #{proposalId}",
            "confirm_match" => proposalId is null
                ? "Confirm a match"
                : $"Confirm your match for proposal #{proposalId}",
            "admin_assign" => (proposalId, supervisorId) switch
            {
                (null, _) => "Assign a supervisor to a proposal",
                (_, null) => $"Assign a supervisor to proposal #{proposalId}",
                _ => $"Assign supervisor {supervisorId} to proposal #{proposalId}",
            },
            _ => toolName,
        };
    }

    private static ChatToolContext? BuildContext(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }
        var name = user.Identity?.Name ?? "";
        var role = Roles.All.FirstOrDefault(user.IsInRole);
        return role is null ? null : new ChatToolContext(id, name, role);
    }

    private string BuildSystemPrompt(ChatToolContext ctx)
    {
        _cachedSystemPromptTemplate ??= LoadSystemPromptTemplate();
        return _cachedSystemPromptTemplate
            .Replace("{{DisplayName}}", string.IsNullOrWhiteSpace(ctx.DisplayName) ? "the signed-in user" : ctx.DisplayName)
            .Replace("{{Role}}", ctx.Role)
            .Replace("{{UtcDate}}", DateTime.UtcNow.ToString("yyyy-MM-dd"));
    }

    private string LoadSystemPromptTemplate()
    {
        var path = Path.IsPathRooted(_options.SystemPromptPath)
            ? _options.SystemPromptPath
            : Path.Combine(env.ContentRootPath, _options.SystemPromptPath);

        if (!File.Exists(path))
        {
            logger.LogWarning("System prompt file not found at {Path}; using fallback.", path);
            return "You are the Blind-Match assistant. The signed-in user is {{DisplayName}}, role {{Role}}. Today is {{UtcDate}}. Use only the provided tools.";
        }
        return File.ReadAllText(path);
    }

    private static List<GroqMessage> ToGroqMessages(string systemPrompt, IList<ChatMessage> history)
    {
        var messages = new List<GroqMessage>
        {
            new() { Role = "system", Content = systemPrompt },
        };

        foreach (var message in history)
        {
            if (message.Role == ChatRole.User)
            {
                var toolResults = message.Content
                    .Where(b => b.Type == ChatContentType.ToolResult)
                    .ToList();
                foreach (var tr in toolResults)
                {
                    messages.Add(new GroqMessage
                    {
                        Role = "tool",
                        ToolCallId = tr.ToolUseId ?? "",
                        Content = tr.ToolResultContent ?? "",
                    });
                }

                var userText = string.Concat(message.Content
                    .Where(b => b.Type == ChatContentType.Text)
                    .Select(b => b.Text ?? ""));
                if (!string.IsNullOrEmpty(userText))
                {
                    messages.Add(new GroqMessage
                    {
                        Role = "user",
                        Content = userText,
                    });
                }
            }
            else
            {
                var assistantText = string.Concat(message.Content
                    .Where(b => b.Type == ChatContentType.Text)
                    .Select(b => b.Text ?? ""));
                var toolCalls = message.Content
                    .Where(b => b.Type == ChatContentType.ToolUse)
                    .Select(b => new GroqToolCall
                    {
                        Id = b.ToolUseId ?? "",
                        Type = "function",
                        Function = new GroqFunctionCall
                        {
                            Name = b.ToolName ?? "",
                            Arguments = b.ToolInput.HasValue ? b.ToolInput.Value.GetRawText() : "{}",
                        },
                    })
                    .ToList();

                messages.Add(new GroqMessage
                {
                    Role = "assistant",
                    Content = string.IsNullOrEmpty(assistantText) ? null : assistantText,
                    ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                });
            }
        }

        return messages;
    }
}
