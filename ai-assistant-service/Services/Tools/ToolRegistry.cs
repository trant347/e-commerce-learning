using System.Collections.Concurrent;
using System.Text.Json;
using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services.Tools;

/// <summary>
/// Central registry of all available tools.
/// Inject IToolDefinition implementations via DI and this registry discovers them automatically.
/// Remote MCP tools can be added at runtime via <see cref="Register"/>.
/// </summary>
public sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, IToolDefinition> _tools;

    public ToolRegistry(IEnumerable<IToolDefinition> tools)
    {
        _tools = new ConcurrentDictionary<string, IToolDefinition>(
            tools.ToDictionary(t => t.Name),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>All registered tools — used to build the Ollama "tools" array.</summary>
    public IReadOnlyCollection<IToolDefinition> All => _tools.Values.ToList();

    /// <summary>Look up a tool by the name the LLM returned in its tool_call.</summary>
    public IToolDefinition? Get(string name)
        => _tools.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>
    /// Dynamically register (or replace) a tool at runtime.
    /// Used by MCP tool discovery to add remotely-discovered tools.
    /// </summary>
    public void Register(IToolDefinition tool)
        => _tools[tool.Name] = tool;

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
