using System.Diagnostics;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services.ChatModels;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Telemetry;
using ai_assistant_service.Services.Tools;
using ai_assistant_service.Services.Mcp;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Services;

public sealed class AiAssistantService : IAiAssistantService
{
    private readonly ITaskChatModelResolver _modelResolver;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<AiAssistantService> _logger;
    private readonly AssistantPrompt _prompt;
    private readonly CategoryResolutionOptions _categoryOptions;
    private readonly CategoryCatalogSnapshotStore? _snapshots;
    private readonly ISearchOrchestrator? _searchOrchestrator;
    private readonly int _historyMessageLimit;

    // Guard against infinite tool-call loops
    private const int MaxToolRounds = 5;
    private const string CategoryFirstSystemPrompt =
        "You are a TaskMaster marketplace assistant. Use only live tool results from this request for provider or booking claims. " +
        "Never invent provider names, rates, ratings, locations, availability or booking details. " +
        "The application decides and locks marketplace category searches; do not select, replace or broaden a category, " +
        "and do not present preferences as verified constraints. Present providers in the returned order without reranking or omitting them. " +
        "If a tool returns no results, say so honestly. " +
        "Use independent tools for booking questions; never claim a provider or booking was verified without its tool result.";

    public AiAssistantService(
        AssistantPrompt prompt,
        ITaskChatModelResolver modelResolver,
        ToolRegistry toolRegistry,
        ILogger<AiAssistantService> logger,
        IOptions<CategoryResolutionOptions>? categoryOptions = null,
        CategoryCatalogSnapshotStore? snapshots = null,
        ISearchOrchestrator? searchOrchestrator = null,
        IOptions<AiChatOptions>? aiChatOptions = null)
    {
        _prompt = prompt;
        _modelResolver = modelResolver;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _categoryOptions = categoryOptions?.Value ?? new CategoryResolutionOptions();
        _snapshots = snapshots;
        _searchOrchestrator = searchOrchestrator;
        _historyMessageLimit = aiChatOptions?.Value.HistoryMessageLimit
            ?? AiChatOptions.DefaultHistoryMessageLimit;
    }

    public async Task<ChatResponse> ChatAsync(
        ChatRequest request,
        ToolExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = _modelResolver.Resolve("chat");
        using var telemetry = AiAssistantTelemetry.StartChat(client.Profile, cancellationToken);
        using var deadline = TaskModelRequestDeadline.Create(client.Profile, cancellationToken);
        var elapsed = Stopwatch.StartNew();
        try
        {
            return await RunConversationAsync(
                request, executionContext, client, telemetry, elapsed, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FailureResponse(client.Profile, telemetry, ChatModelFailureCategory.Timeout);
        }
        catch (ChatModelException exception)
            when (exception.Category == ChatModelFailureCategory.CallerCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(
                "The chat model request was canceled.",
                exception,
                cancellationToken);
        }
        catch (ChatModelException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FailureResponse(client.Profile, telemetry, exception.Category, exception.Metadata);
        }
        catch (JevDecisionException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            telemetry.Failed(ChatModelFailureCategory.ProviderUnavailable, exception.HttpStatusCode);
            _logger.LogWarning(
                "Category decision failed: {Category}, status={Status}",
                exception.Category, exception.HttpStatusCode);
            return SafeAnswer(
                client.Profile.Model,
                "Category matching is temporarily unavailable. Please try again shortly.");
        }
        catch (RoundBudgetExceededException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            telemetry.Failed(ChatModelFailureCategory.RoundBudgetExhausted, null);
            _logger.LogWarning("Request-wide decision/model round budget exhausted");
            return SafeAnswer(client.Profile.Model,
                "I couldn't finish matching this request. Please try rephrasing.");
        }
    }

    private async Task<ChatResponse> RunConversationAsync(
        ChatRequest request,
        ToolExecutionContext executionContext,
        ITaskChatModelClient client,
        AiAssistantTelemetry.ChatTelemetry telemetry,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting chat request with scope count={ScopeCount}, message length={MessageLength}",
            executionContext.AuthorizedScopes.Count,
            request.Message?.Length ?? 0);

        var model = client.Profile.Model;
        var toolDefinitions = _toolRegistry.All
            .Select(t => new ChatModelTool(
                t.Name, t.Description, JsonSerializer.SerializeToElement(t.ParametersSchema)))
            .ToList();

        _logger.LogInformation("Registered {ToolCount} tools: {ToolNames}", 
            toolDefinitions.Count, 
            string.Join(", ", _toolRegistry.All.Select(t => t.Name)));

        var richMode = CategoryResolutionModes.IsRichCatalogMode(_categoryOptions.Mode);
        var messages = richMode
            ? new List<ChatModelMessage> { new(ChatModelRoles.System, CategoryFirstSystemPrompt) }
            : new List<ChatModelMessage>(_prompt.Messages);
        var usedCallIds = messages.SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.Id).ToHashSet(StringComparer.Ordinal);

        // Only user-authored messages are decision/filter context; never seeded examples or tool results.
        var userHistory = request.History?
            .Where(h => h.Role == ChatModelRoles.User && !string.IsNullOrWhiteSpace(h.Content))
            .Select(h => h.Content).ToArray() ?? [];
        var userMessages = new List<string?> { request.Message };
        userMessages.AddRange(userHistory);

        if (_historyMessageLimit > 0 && request.History is { Count: > 0 })
        {
            var recent = request.History.Count > _historyMessageLimit
                ? request.History.Skip(request.History.Count - _historyMessageLimit).ToList()
                : request.History;

            foreach (var h in recent)
            {
                if (!string.IsNullOrWhiteSpace(h.Content) 
                    && (h.Role == "user" || h.Role == "assistant"))
                {
                    messages.Add(new ChatModelMessage(h.Role, h.Content));
                }
            }
        }

        messages.Add(new ChatModelMessage(ChatModelRoles.User, request.Message));
        var hadToolExecutionFailure = false;
        var executedToolNames = new List<string>();
        var executedToolMessages = new List<ChatModelMessage>();
        string? finalContent = null;
        var budget = new RequestRoundBudget(MaxToolRounds);
        CategoryCatalogSnapshot? capturedCatalog = null;
        SearchResult? searchResult = null;
        if (richMode && _snapshots?.TryCaptureUsable(out capturedCatalog, out _) == true)
        {
            if (_searchOrchestrator is null)
                throw new ChatModelException(ChatModelFailureCategory.InvalidConfiguration);

            searchResult = await _searchOrchestrator.RunAsync(
                new SearchRequestContext(
                    request, userHistory, capturedCatalog!, client,
                    () => TimeSpan.FromSeconds(client.Profile.RequestTimeoutSeconds) - elapsed.Elapsed,
                    budget, executionContext),
                cancellationToken);
            switch (searchResult)
            {
                case SearchResult.NeedsClarification or SearchResult.NoMatch:
                    telemetry.Succeeded(false);
                    return SafeAnswer(model, SearchSafeAnswer(searchResult));
                case SearchResult.Unavailable:
                    return SafeAnswer(model, SearchSafeAnswer(searchResult));
                case SearchResult.Results search:
                {
                    var prefetchCall = new ChatModelToolCall(
                        $"catalog-search-{Guid.NewGuid():N}",
                        SearchFilterGrounding.SearchToolName, search.Arguments);
                    messages.Add(new ChatModelMessage(ChatModelRoles.Assistant, null, [prefetchCall]));
                    var prefetchResult = new ChatModelMessage(ChatModelRoles.Tool, search.Content, toolCallId: prefetchCall.Id);
                    messages.Add(prefetchResult);
                    executedToolMessages.Add(prefetchResult);
                    executedToolNames.Add(SearchFilterGrounding.SearchToolName);
                    telemetry.ToolCallCompleted(SearchFilterGrounding.SearchToolName, succeeded: true);
                    usedCallIds.Add(prefetchCall.Id);
                    messages.Add(new ChatModelMessage(ChatModelRoles.System,
                        "The preceding live search used the validated category and explicit hard filters. " +
                        "Use only these current results for provider claims. Do not present soft preferences as verified constraints " +
                        "or claim availability. Do not call search_task_masters again for this request."));
                    if (search.SoftPreferenceContext is not null)
                        messages.Add(new ChatModelMessage(ChatModelRoles.User,
                            search.SoftPreferenceContext));
                    break;
                }
                case SearchResult.NotApplicable:
                    break;
                default:
                    throw new InvalidOperationException("Unsupported search outcome.");
            }
        }
        else
        {
            capturedCatalog = null;
        }

        // Tool-calling loop
        for (int round = 0; round < MaxToolRounds; round++)
        {
            _logger.LogInformation("Starting tool-calling round {Round}/{MaxRounds}, message count={MessageCount}", 
                round + 1, MaxToolRounds, messages.Count);

            cancellationToken.ThrowIfCancellationRequested();
            if (!budget.TrySpend()) break;
            telemetry.RoundStarted();
            var response = await client.ChatAsync(messages, toolDefinitions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var assistantMsg = response.Message;
            if (assistantMsg.Role != ChatModelRoles.Assistant
                || (assistantMsg.ToolCalls ?? []).Any(call => !usedCallIds.Add(call.Id)))
            {
                throw new ChatModelException(ChatModelFailureCategory.ProtocolError);
            }
            messages.Add(assistantMsg);

            // No tool calls → model has produced its final answer
            if (assistantMsg.ToolCalls is not { Count: > 0 })
            {
                _logger.LogInformation("No tool calls in round {Round}, ending loop with final answer", round + 1);
                finalContent = assistantMsg.Content;
                break;
            }

            _logger.LogInformation("Round {Round}: Assistant requested {ToolCallCount} tool calls", 
                round + 1, assistantMsg.ToolCalls.Count);

            // Execute each requested tool and feed results back
            foreach (var toolCall in assistantMsg.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toolName = toolCall.Name;
                if (richMode && string.Equals(
                        toolName, SearchFilterGrounding.SearchToolName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (searchResult is not SearchResult.Results)
                    {
                        if (capturedCatalog is null || _searchOrchestrator is null)
                            return SafeAnswer(model, "Category matching is temporarily unavailable. Please try again shortly.");
                        searchResult = await _searchOrchestrator.RunAsync(
                            new SearchRequestContext(
                                request, userHistory, capturedCatalog, client,
                                () => TimeSpan.FromSeconds(client.Profile.RequestTimeoutSeconds) - elapsed.Elapsed,
                                budget, executionContext),
                            cancellationToken);
                        if (searchResult is not SearchResult.Results search)
                        {
                            if (searchResult is not SearchResult.Unavailable)
                                telemetry.Succeeded(false);
                            return SafeAnswer(model, SearchSafeAnswer(searchResult, lateSearch: true));
                        }
                        var liveToolMessage = new ChatModelMessage(
                            ChatModelRoles.Tool, search.Content, toolCallId: toolCall.Id);
                        messages.Add(liveToolMessage);
                        executedToolMessages.Add(liveToolMessage);
                        executedToolNames.Add(SearchFilterGrounding.SearchToolName);
                        telemetry.ToolCallCompleted(SearchFilterGrounding.SearchToolName, succeeded: true);
                        if (search.SoftPreferenceContext is not null)
                            messages.Add(new ChatModelMessage(ChatModelRoles.User,
                                search.SoftPreferenceContext));
                        continue;
                    }

                    var lockedMessage = new ChatModelMessage(
                        ChatModelRoles.Tool,
                        "{\"error\":\"category_search_already_executed\",\"message\":\"Use the live search results already returned for this request.\"}",
                        toolCallId: toolCall.Id);
                    messages.Add(lockedMessage);
                    continue;
                }
                var args = SearchFilterGrounding.RemoveUngroundedFilters(
                    toolName, toolCall.Arguments, userMessages, out var removedFilters);

                if (removedFilters.Count > 0)
                {
                    _logger.LogInformation(
                        "Dropped {FilterCount} search filters not stated by the user",
                        removedFilters.Count);
                }

                _logger.LogInformation("Executing tool={ToolName}", toolName);
                var registeredToolName = _toolRegistry.Get(toolName)?.Name
                    ?? AiAssistantTelemetry.UnregisteredTool;

                string result;
                try
                {
                    result = await _toolRegistry.ExecuteAsync(
                        executionContext,
                        toolName,
                        args,
                        cancellationToken);
                    telemetry.ToolCallCompleted(registeredToolName, succeeded: true);
                    if (!executedToolNames.Contains(toolName, StringComparer.Ordinal))
                        executedToolNames.Add(toolName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    hadToolExecutionFailure = true;
                    telemetry.ToolCallCompleted(registeredToolName, succeeded: false);
                    _logger.LogError(
                        "Tool execution failed for tool={ToolName}, exception type={ExceptionType}",
                        toolName,
                        exception.GetType().Name);
                    // Return a safe tool payload so the chat can continue without crashing the request.
                    result = "{\"error\":\"tool_unavailable\",\"message\":\"Marketplace tool is temporarily unavailable.\"}";
                }

                _logger.LogInformation("Tool={ToolName} returned result length={ResultLength}", 
                    toolName, result?.Length ?? 0);

                cancellationToken.ThrowIfCancellationRequested();
                var toolMessage = new ChatModelMessage(ChatModelRoles.Tool, result, toolCallId: toolCall.Id);
                messages.Add(toolMessage);
                executedToolMessages.Add(toolMessage);
            }
        }

        // Seeded and historical answers are never a fallback for an exhausted live loop.
        const string defaultFallback = "I wasn't able to find a complete answer — the data may be unavailable or the request needs more detail. Please try rephrasing.";

        var roundLimitReached = finalContent is null;
        finalContent ??= defaultFallback;
        if (richMode && capturedCatalog is null && executedToolNames.Count == 0)
            return SafeAnswer(model, "Category matching is temporarily unavailable. Please try again shortly.");

        if (hadToolExecutionFailure && string.Equals(finalContent, defaultFallback, StringComparison.Ordinal))
        {
            finalContent = "I could not access marketplace data right now. Please try again in a moment.";
        }

        _logger.LogInformation("Chat request completed with final answer length={AnswerLength}, total messages={MessageCount}", 
            finalContent.Length, messages.Count);

        var mentions = ExtractMentions(executedToolMessages);
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Extracted {MentionCount} task master mentions from tool results", mentions.Count);

        telemetry.Succeeded(roundLimitReached);
        return new ChatResponse
        {
            Answer   = finalContent,
            Model    = model,
            Sources  = executedToolNames,
            Mentions = mentions
        };
    }

    private static ChatResponse SafeAnswer(string model, string answer) =>
        new() { Answer = answer, Model = model };

    private static string SearchSafeAnswer(SearchResult result, bool lateSearch = false) =>
        result switch
        {
            SearchResult.NeedsClarification { Reason: SearchClarificationReason.Filters }
                when lateSearch => "Could you clarify the required search filters before I search?",
            SearchResult.NeedsClarification or SearchResult.NoMatch or SearchResult.NotApplicable
                when lateSearch => "Which single marketplace service would you like to find?",
            SearchResult.NeedsClarification clarification => clarification.Message,
            SearchResult.NoMatch =>
                "I couldn't match that request to an offered service. Could you describe the service you need?",
            SearchResult.Unavailable =>
                "Marketplace search is temporarily unavailable. Please try again shortly.",
            _ => throw new InvalidOperationException("Search outcome does not have a safe response.")
        };

    private ChatResponse FailureResponse(
        TaskModelProfile profile,
        AiAssistantTelemetry.ChatTelemetry telemetry,
        ChatModelFailureCategory category,
        ChatModelFailureMetadata? metadata = null)
    {
        telemetry.Failed(category, metadata?.HttpStatusCode);
        _logger.LogWarning(
            "Chat failed for task={TaskId}, provider={Provider}, model={Model}, category={Category}, status={Status}, provider request ID={ProviderRequestId}",
            profile.TaskId,
            profile.Provider,
            profile.Model,
            category,
            metadata?.HttpStatusCode,
            metadata?.ProviderRequestId);
        var answer = string.Equals(
            profile.Provider,
            ChatModelProviders.OpenRouter,
            StringComparison.Ordinal)
            ? OpenRouterFailureMessage(category)
            : OllamaFailureMessage(category, metadata?.HttpStatusCode);
        return new ChatResponse { Answer = answer, Model = profile.Model };
    }

    private static string OpenRouterFailureMessage(ChatModelFailureCategory category) =>
        category switch
        {
            ChatModelFailureCategory.Timeout
                or ChatModelFailureCategory.RateLimited
                or ChatModelFailureCategory.TemporaryInFlightBudget
                or ChatModelFailureCategory.ProviderUnavailable =>
                "The AI assistant is temporarily busy. Please try again shortly.",
            ChatModelFailureCategory.PolicyRejected =>
                "The AI assistant could not process that request.",
            ChatModelFailureCategory.ProtocolError =>
                "The AI assistant returned an invalid response. Please try again.",
            _ => "The AI assistant is unavailable right now."
        };

    private static string OllamaFailureMessage(
        ChatModelFailureCategory category,
        int? httpStatusCode) =>
        category switch
        {
            ChatModelFailureCategory.LocalModelUnavailable =>
                "The AI model is still being set up and isn't available yet. Please try again in a few minutes.",
            ChatModelFailureCategory.Timeout => "The AI model took too long to respond. Please try again.",
            ChatModelFailureCategory.ProviderUnavailable when httpStatusCode is null =>
                "I could not reach the AI model right now. Please try again shortly.",
            ChatModelFailureCategory.ProtocolError =>
                "The AI assistant returned an invalid response. Please try again.",
            _ => "The AI model ran into a problem answering your request. Please try again shortly."
        };

    /// <summary>
    /// Scans tool-role messages for JSON arrays of task masters and extracts id+name pairs.
    /// The product-service returns a JSON array of TaskMaster objects, each with "id" and "name".
    /// </summary>
    private List<TaskMasterMention> ExtractMentions(IEnumerable<ChatModelMessage> messages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mentions = new List<TaskMasterMention>();

        foreach (var msg in messages.Where(m => m.Role == "tool" && !string.IsNullOrWhiteSpace(m.Content)))
        {
            try
            {
                var trimmed = msg.Content!.Trim();

                // Parse into a cloned root so we can safely dispose the document.
                JsonElement root;
                using (var doc = JsonDocument.Parse(trimmed))
                {
                    root = doc.RootElement.Clone();
                }

                // MCP tool results often arrive double-encoded: the JSON array/object
                // is wrapped in a JSON string (ValueKind=String containing "[{...}]").
                // Unwrap one level so the switch below sees Array/Object.
                if (root.ValueKind == JsonValueKind.String)
                {
                    var inner = root.GetString();
                    if (!string.IsNullOrWhiteSpace(inner))
                    {
                        using var innerDoc = JsonDocument.Parse(inner);
                        root = innerDoc.RootElement.Clone();
                    }
                }

                IEnumerable<JsonElement> items = root.ValueKind switch
                {
                    JsonValueKind.Array => root.EnumerateArray().Select(e => e),
                    JsonValueKind.Object when root.TryGetProperty("taskMasters", out var arr)
                        => arr.EnumerateArray().Select(e => e),
                    // Single object with id+name (e.g. get_task_master_by_id result)
                    JsonValueKind.Object when root.TryGetProperty("id", out _)
                        => new[] { root },
                    _ => Enumerable.Empty<JsonElement>()
                };

                foreach (var item in items)
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    if (item.TryGetProperty("id", out var idEl) &&
                        item.TryGetProperty("name", out var nameEl))
                    {
                        var id   = idEl.GetString()   ?? string.Empty;
                        var name = nameEl.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name) && seen.Add(id))
                            mentions.Add(new TaskMasterMention { Id = id, Name = name });
                    }
                }
            }
            catch (JsonException)
            {
                _logger.LogWarning("Tool result could not be parsed while extracting mentions");
            }
        }

        return mentions;
    }
}
