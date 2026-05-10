namespace ai_assistant_service.Services.Tools;

/// <summary>
/// Defines a single callable tool that the LLM can invoke (MCP-style tool-calling).
/// Each implementation represents one capability (e.g. search task masters, get bookings).
/// </summary>
public interface IToolDefinition
{
    /// <summary>Unique machine-readable name sent to the LLM (e.g. "search_task_masters").</summary>
    string Name { get; }

    /// <summary>Human-readable description that helps the LLM decide when to use this tool.</summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema object describing the tool's parameters.
    /// Serialised directly into the Ollama /api/chat "tools" array.
    /// </summary>
    object ParametersSchema { get; }

    /// <summary>
    /// Execute the tool with the arguments the LLM provided.
    /// </summary>
    /// <param name="arguments">Key-value pairs parsed from the LLM's tool_call arguments JSON.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Plain-text result to feed back to the LLM as a tool message.</returns>
    Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken);
}
