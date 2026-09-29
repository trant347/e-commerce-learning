using System.Text.Json;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services.ChatModels;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Tools;

namespace ai_assistant_service.Services;

public sealed class AiAssistantService : IAiAssistantService
{
    private readonly ITaskChatModelResolver _modelResolver;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<AiAssistantService> _logger;
    private readonly AssistantPrompt _prompt;

    // To keep prompts concise, only include the last 2 messages from history (if any) as context for the model.
    private const int MaxChatMemorySize = 2;

    // Guard against infinite tool-call loops
    private const int MaxToolRounds = 5;

    public AiAssistantService(
        AssistantPrompt prompt,
        ITaskChatModelResolver modelResolver,
        ToolRegistry toolRegistry,
        ILogger<AiAssistantService> logger)
    {
        _prompt = prompt;
        _modelResolver = modelResolver;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    public async Task<ChatResponse> ChatAsync(
        ChatRequest request,
        ToolExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = _modelResolver.Resolve("chat");
        using var deadline = TaskModelRequestDeadline.Create(client.Profile, cancellationToken);
        try
        {
            return await RunConversationAsync(request, executionContext, client, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FailureResponse(client.Profile, ChatModelFailureCategory.Timeout);
        }
        catch (ChatModelException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FailureResponse(client.Profile, exception.Category, exception.Metadata.HttpStatusCode);
        }
    }

    private async Task<ChatResponse> RunConversationAsync(
        ChatRequest request,
        ToolExecutionContext executionContext,
        ITaskChatModelClient client,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting chat request for actor={Actor}, scope count={ScopeCount}, message length={MessageLength}",
            executionContext.Actor,
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

        var messages = new List<ChatModelMessage>(_prompt.Messages);
        var usedCallIds = messages.SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.Id).ToHashSet(StringComparer.Ordinal);

        // User-authored text the model saw in this request. Search filters must come from here,
        // never from seeded few-shot examples.
        var userMessages = new List<string?> { request.Message };

        // Include only the last exchange (up to 2 messages) for multi-turn context
        if (request.History is { Count: > 0 })
        {
            var recent = request.History.Count > 2
                ? request.History.Skip(request.History.Count - MaxChatMemorySize).ToList()
                : request.History;

            foreach (var h in recent)
            {
                if (!string.IsNullOrWhiteSpace(h.Content) 
                    && (h.Role == "user" || h.Role == "assistant"))
                {
                    messages.Add(new ChatModelMessage(h.Role, h.Content));
                    if (h.Role == "user")
                    {
                        userMessages.Add(h.Content);
                    }
                }
            }
        }

        messages.Add(new ChatModelMessage(ChatModelRoles.User, request.Message));
        var hadToolExecutionFailure = false;
        var executedToolNames = new List<string>();
        var executedToolMessages = new List<ChatModelMessage>();
        string? finalContent = null;

        // Tool-calling loop
        for (int round = 0; round < MaxToolRounds; round++)
        {
            _logger.LogInformation("Starting tool-calling round {Round}/{MaxRounds}, message count={MessageCount}", 
                round + 1, MaxToolRounds, messages.Count);

            cancellationToken.ThrowIfCancellationRequested();
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
                var args = SearchFilterGrounding.RemoveUngroundedFilters(
                    toolName, toolCall.Arguments, userMessages, out var removedFilters);

                if (removedFilters.Count > 0)
                {
                    _logger.LogInformation(
                        "Dropped search filters not stated by the user: {Filters}",
                        string.Join(", ", removedFilters));
                }

                _logger.LogInformation("Executing tool={ToolName}", toolName);

                string result;
                try
                {
                    result = await _toolRegistry.ExecuteAsync(
                        executionContext,
                        toolName,
                        args,
                        cancellationToken);
                    if (!executedToolNames.Contains(toolName, StringComparer.Ordinal))
                        executedToolNames.Add(toolName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    hadToolExecutionFailure = true;
                    _logger.LogError(ex, "Tool execution failed for tool={ToolName}", toolName);
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

        finalContent ??= defaultFallback;

        if (hadToolExecutionFailure && string.Equals(finalContent, defaultFallback, StringComparison.Ordinal))
        {
            finalContent = "I could not access marketplace data right now. Please try again in a moment.";
        }

        _logger.LogInformation("Chat request completed with final answer length={AnswerLength}, total messages={MessageCount}", 
            finalContent.Length, messages.Count);

        var mentions = ExtractMentions(executedToolMessages);
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Extracted {MentionCount} task master mentions from tool results", mentions.Count);

        return new ChatResponse
        {
            Answer   = finalContent,
            Model    = model,
            Sources  = executedToolNames,
            Mentions = mentions
        };
    }

    private ChatResponse FailureResponse(
        TaskModelProfile profile, ChatModelFailureCategory category, int? httpStatusCode = null)
    {
        _logger.LogWarning(
            "Chat failed for task={TaskId}, provider={Provider}, model={Model}, category={Category}, status={Status}",
            profile.TaskId, profile.Provider, profile.Model, category, httpStatusCode);
        var answer = category switch
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
        return new ChatResponse { Answer = answer, Model = profile.Model };
    }

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

                _logger.LogDebug("[ExtractMentions] Tool message content ({Length} chars): {Content}",
                    trimmed.Length, trimmed.Length > 500 ? trimmed[..500] + "..." : trimmed);

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
                        _logger.LogDebug("[ExtractMentions] Unwrapping double-encoded JSON string");
                        using var innerDoc = JsonDocument.Parse(inner);
                        root = innerDoc.RootElement.Clone();
                    }
                }

                _logger.LogDebug("[ExtractMentions] Parsed JSON, ValueKind={ValueKind}", root.ValueKind);

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
                        _logger.LogDebug("[ExtractMentions] Found candidate: id={Id}, name={Name}", id, name);
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name) && seen.Add(id))
                            mentions.Add(new TaskMasterMention { Id = id, Name = name });
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("[ExtractMentions] Tool content was not valid JSON: {Message}", ex.Message);
            }
        }

        return mentions;
    }
}
