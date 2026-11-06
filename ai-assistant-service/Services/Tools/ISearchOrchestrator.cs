using System.Text.Json;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Mcp;

namespace ai_assistant_service.Services.Tools;

public sealed record SearchRequestContext(
    ChatRequest Request,
    IReadOnlyList<string> UserHistory,
    CategoryCatalogSnapshot Catalog,
    ITaskChatModelClient Client,
    Func<TimeSpan> RemainingTime,
    RequestRoundBudget Budget,
    ToolExecutionContext ExecutionContext);

public enum SearchClarificationReason
{
    Category,
    Filters
}

public abstract record SearchResult
{
    private SearchResult() { }

    public sealed record Results(
        IReadOnlyDictionary<string, JsonElement> Arguments,
        string Content,
        string? SoftPreferenceContext = null) : SearchResult;

    public sealed record NeedsClarification(
        SearchClarificationReason Reason,
        string Message) : SearchResult;

    public sealed record NoMatch : SearchResult;

    public sealed record Unavailable : SearchResult;

    public sealed record NotApplicable : SearchResult;
}

public interface ISearchOrchestrator
{
    Task<SearchResult> RunAsync(
        SearchRequestContext context,
        CancellationToken cancellationToken = default);
}
