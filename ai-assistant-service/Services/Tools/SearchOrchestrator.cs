using System.Text.Json;

namespace ai_assistant_service.Services.Tools;

public sealed class SearchOrchestrator(
    CategoryDecisionService categoryDecisions,
    SearchFilterExtractionService filterExtraction,
    ToolRegistry toolRegistry,
    ILogger<SearchOrchestrator> logger) : ISearchOrchestrator
{
    public async Task<SearchResult> RunAsync(
        SearchRequestContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = await categoryDecisions.ResolveAsync(
            context.Request, context.UserHistory, context.Catalog, context.Client,
            context.RemainingTime, context.Budget, cancellationToken);
        switch (decision.Status)
        {
            case CategoryDecisionStatus.NeedsClarification:
                return new SearchResult.NeedsClarification(
                    SearchClarificationReason.Category, decision.Clarification!);
            case CategoryDecisionStatus.NoMatch:
                return new SearchResult.NoMatch();
            case CategoryDecisionStatus.NotApplicable:
                return new SearchResult.NotApplicable();
            case CategoryDecisionStatus.Resolved:
                break;
            default:
                throw new InvalidOperationException("Unsupported category decision status.");
        }

        var filters = await filterExtraction.ExtractAsync(
            context.Request.Message ?? string.Empty, context.UserHistory,
            context.Client, context.Budget, cancellationToken);
        if (filters.NeedsClarification)
            return new SearchResult.NeedsClarification(
                SearchClarificationReason.Filters,
                "Could you clarify the required location, hourly USD rate, rating, or other requirement before I search?");
        if (toolRegistry.Get(SearchFilterGrounding.SearchToolName) is null)
            return new SearchResult.Unavailable();

        var arguments = LockedSearchArguments(decision.CategoryId!, filters);
        var result = await ExecuteLockedSearchAsync(
            context.ExecutionContext, arguments, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (SearchFailed(result))
        {
            logger.LogWarning("Locked category search returned an unusable result");
            return new SearchResult.Unavailable();
        }

        var softPreferenceContext = filters.SoftPreferences.Count > 0
            ? JsonSerializer.Serialize(new
            {
                contextFromEarlierUserMessages = "These are preferences, not verified provider constraints.",
                softPreferences = filters.SoftPreferences.Select(item => new
                {
                    item.Field, item.Value, item.Unit, item.Evidence
                })
            })
            : null;
        return new SearchResult.Results(arguments, result, softPreferenceContext);
    }

    private static IReadOnlyDictionary<string, JsonElement> LockedSearchArguments(
        string categoryId, SearchFilterEnvelope filters)
    {
        var arguments = new Dictionary<string, JsonElement>(filters.HardArguments, StringComparer.Ordinal)
        {
            ["category"] = JsonSerializer.SerializeToElement(categoryId)
        };
        return arguments;
    }

    private async Task<string> ExecuteLockedSearchAsync(
        ToolExecutionContext executionContext,
        IReadOnlyDictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await toolRegistry.ExecuteAsync(
                executionContext, SearchFilterGrounding.SearchToolName, arguments, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                "Locked category search failed: exception type={ExceptionType}",
                exception.GetType().Name);
            return "{\"error\":\"tool_unavailable\"}";
        }
    }

    private static bool SearchFailed(string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return true;
        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(root.GetString() ?? string.Empty);
                root = inner.RootElement;
                return root.ValueKind != JsonValueKind.Array;
            }
            return root.ValueKind != JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
