namespace ai_assistant_service.Services.Mcp;

/// <summary>Outcome of one <see cref="McpCategoryEnumRefresher.RefreshAsync"/> call.</summary>
public enum CategoryRefreshResult
{
    /// <summary>The search tool schema now carries a new category list.</summary>
    Updated,

    /// <summary>The live catalog matches the list already applied.</summary>
    Unchanged,

    /// <summary>
    /// <c>get_categories</c> threw or timed out, so the MCP session is presumed dead
    /// and should be reconnected.
    /// </summary>
    Failed,

    /// <summary>
    /// <c>get_categories</c> responded but without usable values (empty catalog or
    /// tool error). The session is healthy, so no reconnect is needed.
    /// </summary>
    NoUsableCategories,

    /// <summary>The search or categories tool is not registered.</summary>
    ToolsUnavailable,
}
