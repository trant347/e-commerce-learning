using System.Text.Json;
using System.Text.Json.Serialization;

namespace ai_assistant_service.Services.Contracts;

public interface IOllamaClient
{
    /// <summary>Legacy single-turn generate (kept for compatibility).</summary>
    Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken);

    /// <summary>
    /// Multi-turn chat with optional tool definitions.
    /// Returns the assistant message; tool_calls are populated when the model wants to invoke a tool.
    /// </summary>
    Task<OllamaChatMessage> ChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<object>? tools, CancellationToken cancellationToken);
}

// ── Shared message / tool-call models used by IOllamaClient and AiAssistantService ──

public sealed class OllamaChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;   // "system" | "user" | "assistant" | "tool"

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OllamaToolCall>? ToolCalls { get; set; }
}

public sealed class OllamaToolCall
{
    [JsonPropertyName("function")]
    public OllamaToolCallFunction Function { get; set; } = new();
}

public sealed class OllamaToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Use JsonElement so any JSON value type (string, number, bool, object)
    /// deserialises correctly. Flat string values are extracted in ToolRegistry.ExecuteAsync.
    /// </summary>
    [JsonPropertyName("arguments")]
    public Dictionary<string, JsonElement> Arguments { get; set; } = new();
}
