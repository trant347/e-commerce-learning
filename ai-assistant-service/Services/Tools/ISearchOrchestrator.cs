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

public sealed record SearchResult(
    CategoryDecision Decision,
    SearchFilterEnvelope? Filters = null,
    IReadOnlyDictionary<string, JsonElement>? Arguments = null,
    string? Content = null,
    string? SoftPreferenceContext = null);

public interface ISearchOrchestrator
{
    Task<SearchResult> RunAsync(
        SearchRequestContext context,
        CancellationToken cancellationToken = default);
}
