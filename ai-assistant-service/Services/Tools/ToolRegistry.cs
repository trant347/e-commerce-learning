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
            kvp => FlattenToString(kvp.Value),
            StringComparer.OrdinalIgnoreCase);

        return await tool.ExecuteAsync(flatArgs, cancellationToken);
    }

    /// <summary>
    /// Coerces a JsonElement argument into the plain string the underlying tool expects.
    /// Small models (notably llama3.2:3b) often wrap scalar arguments in a single-element
    /// JSON array — e.g. {"category": ["tutoring"]} instead of {"category": "tutoring"}.
    /// Forwarding the raw text "[\"tutoring\"]" then never matches anything downstream.
    /// Unwrap single-element arrays, join multi-element string arrays with commas, and
    /// drop placeholders like null / "nil" / "none" so the tool receives a clean value.
    /// </summary>
    private static string FlattenToString(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return NormalizePlaceholder(value.GetString() ?? string.Empty);

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return value.ToString();

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return string.Empty;

            case JsonValueKind.Array:
                var items = value.EnumerateArray()
                    .Select(FlattenToString)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                return items.Count switch
                {
                    0 => string.Empty,
                    1 => items[0],
                    _ => string.Join(",", items)
                };

            case JsonValueKind.Object:
                // Objects don't fit our string-arg contract; pass the raw JSON through
                // so the tool can decide what to do (or surface a clear error).
                return value.GetRawText();

            default:
                return value.ToString();
        }
    }

    private static string NormalizePlaceholder(string s)
    {
        var trimmed = s.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (trimmed.Equals("nil", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return trimmed;
    }
}
