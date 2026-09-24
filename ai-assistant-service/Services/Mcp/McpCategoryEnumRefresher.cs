using System.Text.Json;
using ai_assistant_service.Services.Tools;

namespace ai_assistant_service.Services.Mcp;

/// <summary>
/// Keeps the <c>category</c> enum on <c>search_task_masters</c> in sync with the
/// product-service catalog by calling the <c>get_categories</c> MCP tool.
/// Small models (e.g. llama3.2:3b) are far more reliable when the schema lists the
/// legal values directly than when they must chain get_categories → search themselves.
/// </summary>
public sealed class McpCategoryEnumRefresher
{
    internal const string SearchToolName = "search_task_masters";
    internal const string CategoriesToolName = "get_categories";
    internal const string CategoryParameterName = "category";

    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<McpCategoryEnumRefresher> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<string> _appliedCategories = Array.Empty<string>();
    private IParameterEnumConstrainable? _appliedTo;

    public McpCategoryEnumRefresher(
        ToolRegistry toolRegistry,
        ILogger<McpCategoryEnumRefresher> logger)
    {
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    /// <summary>The category list currently applied to the search tool schema.</summary>
    public IReadOnlyList<string> AppliedCategories => _appliedCategories;

    /// <summary>Upper bound for one <c>get_categories</c> call, so a half-open session cannot stall the refresh loop.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Applies the last known category list to a replacement search tool before it
    /// is registered, so chat requests never see a schema without the enum.
    /// </summary>
    public async Task ApplyKnownCategoriesAsync(
        IParameterEnumConstrainable searchTool,
        CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            ApplyKnownCategoriesLocked(searchTool);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void ApplyKnownCategoriesLocked(IParameterEnumConstrainable searchTool)
    {
        if (ReferenceEquals(searchTool, _appliedTo) || _appliedCategories.Count == 0) return;
        searchTool.SetParameterEnum(CategoryParameterName, _appliedCategories);
        _appliedTo = searchTool;
    }

    /// <summary>
    /// Fetches the live catalog and applies it when it differs from the current enum,
    /// or when the search tool was replaced by a reconnect.
    /// Failures and empty results keep the previously applied list.
    /// </summary>
    public async Task<CategoryRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var searchTool = _toolRegistry.Get(SearchToolName) as IParameterEnumConstrainable;
        var categoriesTool = _toolRegistry.Get(CategoriesToolName);
        if (searchTool is null || categoriesTool is null)
        {
            _logger.LogDebug(
                "[MCP Categories] Skipping category enum refresh — required tools not registered");
            return CategoryRefreshResult.ToolsUnavailable;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CallTimeout);
        try
        {
            // Carry the last known list onto a replaced tool even if this fetch fails.
            ApplyKnownCategoriesLocked(searchTool);

            var executionContext = new ToolExecutionContext(
                "system:mcp-discovery",
                Array.Empty<string>(),
                Guid.NewGuid().ToString("N"));
            var raw = await categoriesTool.ExecuteAsync(
                executionContext,
                new Dictionary<string, string>(),
                timeoutCts.Token);

            var categories = ParseCategories(raw);
            if (categories.Count == 0)
            {
                _logger.LogWarning(
                    "[MCP Categories] get_categories returned no usable values — keeping {Count} existing categories",
                    _appliedCategories.Count);
                return CategoryRefreshResult.NoUsableCategories;
            }

            if (ReferenceEquals(searchTool, _appliedTo)
                && categories.SequenceEqual(_appliedCategories, StringComparer.OrdinalIgnoreCase))
            {
                return CategoryRefreshResult.Unchanged;
            }

            searchTool.SetParameterEnum(CategoryParameterName, categories);
            var previousCount = _appliedCategories.Count;
            _appliedCategories = categories;
            _appliedTo = searchTool;
            _logger.LogInformation(
                "[MCP Categories] Updated search_task_masters category enum: {PreviousCount} → {Count} categories",
                previousCount, categories.Count);
            return CategoryRefreshResult.Updated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[MCP Categories] get_categories timed out after {Timeout}s — keeping {Count} existing categories",
                CallTimeout.TotalSeconds, _appliedCategories.Count);
            return CategoryRefreshResult.Failed;
        }
        catch (Exception ex)
        {
            // Logged without the stack trace: this repeats every interval while
            // product-service is down. The full exception is available at Debug.
            _logger.LogWarning(
                "[MCP Categories] Failed to refresh category enum ({Error}) — keeping {Count} existing categories",
                ex.Message.Length > 200 ? ex.Message[..200] + "…" : ex.Message, _appliedCategories.Count);
            _logger.LogDebug(ex, "[MCP Categories] Category refresh failure details");
            return CategoryRefreshResult.Failed;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static IReadOnlyList<string> ParseCategories(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // MCP wraps a tool's String result as a JSON-encoded string, so we may
            // receive "[\"a\",\"b\"]" (a string) instead of ["a","b"] (an array).
            // Unwrap one layer and re-parse if needed.
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner)) return Array.Empty<string>();
                using var innerDoc = JsonDocument.Parse(inner);
                return ExtractStringArray(innerDoc.RootElement);
            }

            return ExtractStringArray(root);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ExtractStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        return element.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
