namespace project_approval_system.Services.Chatbot;

public sealed class GroqOptions
{
    public const string SectionName = "Groq";

    public string Model { get; set; } = "qwen/qwen3.8-27b";

    public int MaxTokens { get; set; } = 2048;

    public string BaseUrl { get; set; } = "https://api.groq.com/openai";

    public string SystemPromptPath { get; set; } = "Services/Chatbot/system-prompt.md";

    public int MaxToolIterations { get; set; } = 8;
}
