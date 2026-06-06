using System.Text.Json;
using ai_assistant_service.Services.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ai_assistant_service.Services.Mcp;

/// <summary>
/// Wraps a remote MCP tool as a local <see cref="IToolDefinition"/>.
/// The tool metadata (name, description, schema) comes from the remote MCP server;
/// execution is forwarded via <see cref="McpClientTool.CallAsync"/>.
/// </summary>
public sealed class McpRemoteTool : IToolDefinition
{
    private readonly McpClientTool _mcpTool;
    private readonly object _parametersSchema;

    public McpRemoteTool(McpClientTool mcpTool)
    {
        _mcpTool = mcpTool;

        // Normalize the MCP JSON Schema into the simple format Ollama expects.
        // MCP/Spring AI schemas may include $schema, additionalProperties, etc.
        // that confuse smaller LLMs. Extract only type/properties/required.
        _parametersSchema = NormalizeSchema(mcpTool.JsonSchema);
    }

    public string Name => _mcpTool.Name;

    public string Description => _mcpTool.Description ?? string.Empty;

    public object ParametersSchema => _parametersSchema;

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken)
    {
        // Convert string arguments to object? for the MCP CallAsync API.
        var mcpArgs = arguments.ToDictionary(
            kvp => kvp.Key,
            kvp => (object?)kvp.Value);

        var result = await _mcpTool.CallAsync(
            mcpArgs,
            cancellationToken: cancellationToken);

        // Extract text content from the CallToolResult.
        return ExtractTextContent(result);
    }

    /// <summary>
    /// Strips MCP/JSON Schema metadata ($schema, additionalProperties, etc.)
    /// down to the minimal {type, properties, required} that Ollama expects.
    /// </summary>
    private static object NormalizeSchema(JsonElement schema)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                var propDict = new Dictionary<string, object>();

                if (prop.Value.TryGetProperty("type", out var typeEl))
                    propDict["type"] = typeEl.GetString() ?? "string";

                if (prop.Value.TryGetProperty("description", out var descEl))
                    propDict["description"] = descEl.GetString() ?? string.Empty;

                if (prop.Value.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
                {
                    propDict["enum"] = enumEl.EnumerateArray()
                        .Select(e => e.GetString() ?? string.Empty)
                        .ToArray();
                }

                properties[prop.Name] = propDict;
            }
        }

        if (schema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reqEl.EnumerateArray())
            {
                var name = item.GetString();
                if (name != null) required.Add(name);
            }
        }

        return new
        {
            type = "object",
            properties,
            required = required.ToArray()
        };
    }

    private static string ExtractTextContent(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
            return string.Empty;

        var textParts = result.Content
            .Where(c => c.Type == "text" || c is TextContentBlock)
            .Select(c => c switch
            {
                TextContentBlock text => text.Text ?? string.Empty,
                _ => c.ToString() ?? string.Empty
            })
            .ToList();

        return textParts.Count > 0
            ? string.Join("\n", textParts)
            : result.Content.First().ToString() ?? string.Empty;
    }
}
