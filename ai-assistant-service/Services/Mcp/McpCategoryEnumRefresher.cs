using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Tools;
using Microsoft.Extensions.Options;

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
    internal const string CategoryCatalogToolName = "get_category_catalog";
    internal const string CategoryParameterName = "category";

    private readonly ToolRegistry _toolRegistry;
    private readonly CategoryResolutionOptions _options;
    private readonly CategoryCatalogSnapshotStore _snapshotStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpCategoryEnumRefresher> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<string> _appliedCategories = Array.Empty<string>();
    private IParameterEnumConstrainable? _appliedTo;

    public McpCategoryEnumRefresher(
        ToolRegistry toolRegistry,
        IOptions<CategoryResolutionOptions> options,
        CategoryCatalogSnapshotStore snapshotStore,
        TimeProvider timeProvider,
        ILogger<McpCategoryEnumRefresher> logger)
    {
        _toolRegistry = toolRegistry;
        _options = options.Value;
        _snapshotStore = snapshotStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The category list currently applied to the search tool schema.</summary>
    public IReadOnlyList<string> AppliedCategories => _appliedCategories;

    public string ActiveCatalogToolName =>
        CategoryResolutionModes.IsRichCatalogMode(_options.Mode)
            ? CategoryCatalogToolName
            : CategoriesToolName;

    /// <summary>Upper bound for one <c>get_categories</c> call, so a half-open session cannot stall the refresh loop.</summary>
    public TimeSpan CallTimeout { get; set; } = DefaultCallTimeout;

    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(15);

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
        var knownCategories = CategoryResolutionModes.IsRichCatalogMode(_options.Mode)
            ? _snapshotStore.Current?.CategoryIds ?? []
            : _appliedCategories;
        if (ReferenceEquals(searchTool, _appliedTo) || knownCategories.Count == 0) return;
        searchTool.SetParameterEnum(CategoryParameterName, knownCategories);
        _appliedCategories = knownCategories;
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
        var catalogTool = _toolRegistry.Get(ActiveCatalogToolName);
        if (searchTool is null || catalogTool is null)
        {
            _logger.LogDebug(
                "[MCP Categories] Skipping category refresh — required tools not registered");
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
            var raw = await catalogTool.ExecuteAsync(
                executionContext,
                new Dictionary<string, string>(),
                timeoutCts.Token);

            return CategoryResolutionModes.IsRichCatalogMode(_options.Mode)
                ? RefreshRichCatalogLocked(searchTool, raw)
                : RefreshLegacyCategoriesLocked(searchTool, raw);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var toolName = ActiveCatalogToolName;
            _snapshotStore.RecordFailure($"{toolName} timed out.");
            _logger.LogWarning(
                "[MCP Categories] {ToolName} timed out after {Timeout}s — keeping the last usable catalog",
                toolName, CallTimeout.TotalSeconds);
            return CategoryRefreshResult.Failed;
        }
        catch (Exception ex)
        {
            _snapshotStore.RecordFailure("Category catalog refresh failed.");
            // Logged without the stack trace: this repeats every interval while
            // product-service is down. The full exception is available at Debug.
            _logger.LogWarning(
                "[MCP Categories] Failed to refresh categories ({Error}) — keeping the last usable catalog",
                ex.Message.Length > 200 ? ex.Message[..200] + "…" : ex.Message);
            _logger.LogDebug(ex, "[MCP Categories] Category refresh failure details");
            return CategoryRefreshResult.Failed;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private CategoryRefreshResult RefreshLegacyCategoriesLocked(
        IParameterEnumConstrainable searchTool,
        string raw)
    {
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

    private CategoryRefreshResult RefreshRichCatalogLocked(
        IParameterEnumConstrainable searchTool,
        string raw)
    {
        if (!CategoryCatalogParser.TryParse(
                raw,
                _options.MaxCatalogContextChars,
                _timeProvider.GetUtcNow(),
                out var snapshot,
                out var error))
        {
            _snapshotStore.RecordFailure(error);
            _logger.LogWarning(
                "[MCP Categories] get_category_catalog returned no usable snapshot ({Reason}) — keeping the last usable catalog",
                error);
            return CategoryRefreshResult.NoUsableCategories;
        }

        var validatedSnapshot = snapshot!;
        var previous = _snapshotStore.Current;
        if (previous is not null
            && string.Equals(
                previous.CatalogVersion,
                validatedSnapshot.CatalogVersion,
                StringComparison.Ordinal)
            && !string.Equals(
                previous.DecisionContext,
                validatedSnapshot.DecisionContext,
                StringComparison.Ordinal))
        {
            const string integrityError =
                "The category catalog content changed without a catalogVersion change.";
            _snapshotStore.RecordFailure(integrityError);
            _logger.LogWarning("[MCP Categories] {Reason}", integrityError);
            return CategoryRefreshResult.NoUsableCategories;
        }

        var metadataChanged = previous is null
            || !string.Equals(
                previous.CatalogVersion,
                validatedSnapshot.CatalogVersion,
                StringComparison.Ordinal);
        var idsChanged = !validatedSnapshot.CategoryIds.SequenceEqual(
            _appliedCategories,
            StringComparer.Ordinal);
        var replacementTool = !ReferenceEquals(searchTool, _appliedTo);

        _snapshotStore.Publish(validatedSnapshot);
        if (idsChanged || replacementTool)
        {
            searchTool.SetParameterEnum(CategoryParameterName, validatedSnapshot.CategoryIds);
            _appliedCategories = validatedSnapshot.CategoryIds;
            _appliedTo = searchTool;
        }

        if (!metadataChanged)
        {
            return CategoryRefreshResult.Unchanged;
        }

        _logger.LogInformation(
            "[MCP Categories] Published category catalog {CatalogVersion} with {Count} categories",
            validatedSnapshot.CatalogVersion, validatedSnapshot.Categories.Length);
        return CategoryRefreshResult.Updated;
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
