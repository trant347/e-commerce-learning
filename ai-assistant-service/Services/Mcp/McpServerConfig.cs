namespace ai_assistant_service.Services.Mcp;

/// <summary>
/// Configuration for a remote MCP server that the AI assistant connects to
/// in order to discover and invoke tools dynamically.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>Human-readable name for logging/diagnostics.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The SSE endpoint URL of the MCP server,
    /// e.g. "http://product-service:8080/sse".
    /// </summary>
    public required string Endpoint { get; init; }
}
