using System.Text.Json;
using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services.Tools;

/// <summary>
/// Central registry of all available tools.
/// Inject IToolDefinition implementations via DI and this registry discovers them automatically.
/// </summary>
public sealed class ToolRegistry
{
    private readonly IReadOnlyDictionary<string, IToolDefinition> _tools;

    public ToolRegistry(IEnumerable<IToolDefinition> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All registered tools — used to build the Ollama "tools" array.</summary>
    public IReadOnlyCollection<IToolDefinition> All => _tools.Values.ToList();

    /// <summary>Look up a tool by the name the LLM returned in its tool_call.</summary>
    public IToolDefinition? Get(string name)
        => _tools.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>
    /// Execute a tool by name. Flattens JsonElement argument values to strings
    /// before passing them to the tool implementation.
    /// </summary>
    public async Task<string> ExecuteAsync(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken)
    {
        var tool = Get(toolName);
        if (tool is null)
            return $"Tool '{toolName}' is not registered.";

        // Flatten JsonElement values → plain strings for the tool to consume
        var flatArgs = arguments.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ValueKind == JsonValueKind.String
                ? kvp.Value.GetString() ?? string.Empty
                : kvp.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        return await tool.ExecuteAsync(flatArgs, cancellationToken);
    }
}
