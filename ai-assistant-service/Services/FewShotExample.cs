namespace ai_assistant_service.Services;

/// <summary>
/// Static few-shot example bound from <c>PromptOptions:FewShotExamples</c> in configuration.
/// Each example is a short scripted conversation that gets prepended to every chat request,
/// teaching the model both the tool-calling channel (use <c>tool_calls</c>, not text) and
/// the desired final-answer style.
/// </summary>
public sealed class FewShotExample
{
    /// <summary>Human-readable label for logs/diagnostics. Not sent to the model.</summary>
    public string? Description { get; set; }

    public List<FewShotTurn> Turns { get; set; } = new();
}

public sealed class FewShotTurn
{
    /// <summary><c>user</c>, <c>assistant</c>, or <c>tool</c>.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Plain text. Leave null on assistant turns that only emit a tool call.</summary>
    public string? Content { get; set; }

    /// <summary>Populated only on assistant turns that demonstrate a tool invocation.</summary>
    public List<FewShotToolCall>? ToolCalls { get; set; }
}

public sealed class FewShotToolCall
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Scalar arguments only. Values are bound as strings from configuration and
    /// converted to JSON string elements when seeded — sufficient for category /
    /// location / id-style parameters which is all the tools currently accept.
    /// </summary>
    public Dictionary<string, string> Arguments { get; set; } = new();
}
