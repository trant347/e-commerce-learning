using System.Text.Json;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Tools;

namespace ai_assistant_service.Services;

public sealed class AiAssistantService : IAiAssistantService
{
    private readonly IConfiguration _configuration;
    private readonly IOllamaClient _ollamaClient;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<AiAssistantService> _logger;
    private readonly Lazy<IReadOnlyList<OllamaChatMessage>> _seededExamples;

    // To keep prompts concise, only include the last 2 messages from history (if any) as context for the model.
    private const int MaxChatMemorySize = 2;

    // Guard against infinite tool-call loops
    private const int MaxToolRounds = 5;

    public AiAssistantService(
        IConfiguration configuration,
        IOllamaClient ollamaClient,
        ToolRegistry toolRegistry,
        ILogger<AiAssistantService> logger)
    {
        _configuration = configuration;
        _ollamaClient = ollamaClient;
        _toolRegistry = toolRegistry;
        _logger = logger;
        _seededExamples = new Lazy<IReadOnlyList<OllamaChatMessage>>(LoadFewShotExamples);
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting chat request with message length={MessageLength}", 
            request.Message?.Length ?? 0);

        var model = _configuration["Ollama:Model"] ?? "llama3.2:3b";
        var systemPrompt = _configuration["PromptOptions:SystemPrompt"]
            ?? "You are a TaskMaster marketplace assistant.";

        _logger.LogInformation("Using model={Model}, systemPrompt length={SystemPromptLength}", 
            model, systemPrompt.Length);

        // Build the Ollama tools array from registered tools
        var toolDefinitions = _toolRegistry.All
            .Select(t => (object)new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.ParametersSchema
                }
            })
            .ToList();

        _logger.LogInformation("Registered {ToolCount} tools: {ToolNames}", 
            toolDefinitions.Count, 
            string.Join(", ", _toolRegistry.All.Select(t => t.Name)));

        _logger.LogDebug("Tool schemas being sent to Ollama: {ToolSchemas}",
            JsonSerializer.Serialize(toolDefinitions));

        // Seed the conversation
        var messages = new List<OllamaChatMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };

        // Append static few-shot examples (loaded once from PromptOptions:FewShotExamples).
        // These teach the model the tool-calling channel and final-answer style without
        // bloating the system prompt or polluting the user-visible chat history.
        var examples = _seededExamples.Value;
        if (examples.Count > 0)
        {
            messages.AddRange(examples);
            _logger.LogDebug("Seeded {Count} few-shot example messages into the conversation", examples.Count);
        }

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
                    messages.Add(new OllamaChatMessage { Role = h.Role, Content = h.Content });
                }
            }
        }

        messages.Add(new OllamaChatMessage { Role = "user", Content = request.Message });
        var hadToolExecutionFailure = false;

        // Tool-calling loop
        for (int round = 0; round < MaxToolRounds; round++)
        {
            _logger.LogInformation("Starting tool-calling round {Round}/{MaxRounds}, message count={MessageCount}", 
                round + 1, MaxToolRounds, messages.Count);

            var assistantMsg = await _ollamaClient.ChatAsync(model, messages, toolDefinitions, cancellationToken);
            messages.Add(assistantMsg);

            // Some small models (e.g. llama3.2:3b) don't fill `tool_calls`; they emit
            // {"name": "...", "parameters": {...}} as plain text in `content` instead.
            // Recover by parsing the content and treating it as a tool call.
            if ((assistantMsg.ToolCalls is null || assistantMsg.ToolCalls.Count == 0)
                && TryParseContentToolCall(assistantMsg.Content, out var inlineCall))
            {
                _logger.LogInformation("Round {Round}: parsed inline tool call from content: {ToolName}",
                    round + 1, inlineCall!.Function.Name);
                assistantMsg.ToolCalls = new List<OllamaToolCall> { inlineCall };
                assistantMsg.Content = null; // suppress the raw JSON from leaking into the final answer
            }

            // No tool calls → model has produced its final answer
            if (assistantMsg.ToolCalls is not { Count: > 0 })
            {
                _logger.LogInformation("No tool calls in round {Round}, ending loop with final answer", round + 1);
                break;
            }

            _logger.LogInformation("Round {Round}: Assistant requested {ToolCallCount} tool calls", 
                round + 1, assistantMsg.ToolCalls.Count);

            // Execute each requested tool and feed results back
            foreach (var toolCall in assistantMsg.ToolCalls)
            {
                var toolName = toolCall.Function.Name;
                var args = toolCall.Function.Arguments;

                _logger.LogInformation("Executing tool={ToolName} with args={Args}", 
                    toolName, string.Join(", ", args.Select(a => $"{a.Key}={a.Value}")));

                string result;
                try
                {
                    result = await _toolRegistry.ExecuteAsync(toolName, args, cancellationToken);
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

                messages.Add(new OllamaChatMessage
                {
                    Role    = "tool",
                    Content = result
                });
            }
        }

        // The last assistant message with real content is the final answer.
        // If the loop exhausted all rounds without a conclusive answer, say so explicitly.
        const string defaultFallback = "I wasn't able to find a complete answer — the data may be unavailable or the request needs more detail. Please try rephrasing.";

        var finalContent = messages
            .LastOrDefault(m => m.Role == "assistant"
                             && m.ToolCalls is not { Count: > 0 }
                             && !string.IsNullOrWhiteSpace(m.Content))
            ?.Content
            ?? defaultFallback;

        if (hadToolExecutionFailure && string.Equals(finalContent, defaultFallback, StringComparison.Ordinal))
        {
            finalContent = "I could not access marketplace data right now. Please try again in a moment.";
        }

        _logger.LogInformation("Chat request completed with final answer length={AnswerLength}, total messages={MessageCount}", 
            finalContent.Length, messages.Count);

        var mentions = ExtractMentions(messages);
        _logger.LogInformation("Extracted {MentionCount} task master mentions from tool results", mentions.Count);

        return new ChatResponse
        {
            Answer   = finalContent,
            Model    = model,
            Sources  = _toolRegistry.All.Select(t => t.Name).ToList(),
            Mentions = mentions
        };
    }

    /// <summary>
    /// Materialises the configured few-shot examples into the message shape the Ollama
    /// chat endpoint expects. Each turn is mapped to an <see cref="OllamaChatMessage"/>;
    /// assistant turns with <c>ToolCalls</c> demonstrate proper use of the native
    /// tool-call channel, which is the format we want the live model to mimic.
    /// </summary>
    private IReadOnlyList<OllamaChatMessage> LoadFewShotExamples()
    {
        var examples = _configuration
            .GetSection("PromptOptions:FewShotExamples")
            .Get<List<FewShotExample>>() ?? new List<FewShotExample>();

        var seeded = new List<OllamaChatMessage>();
        foreach (var example in examples)
        {
            if (example.Turns is not { Count: > 0 }) continue;

            foreach (var turn in example.Turns)
            {
                if (string.IsNullOrWhiteSpace(turn.Role)) continue;

                var msg = new OllamaChatMessage
                {
                    Role = turn.Role,
                    Content = turn.Content
                };

                if (turn.ToolCalls is { Count: > 0 })
                {
                    msg.ToolCalls = turn.ToolCalls
                        .Where(tc => !string.IsNullOrWhiteSpace(tc.Name))
                        .Select(tc => new OllamaToolCall
                        {
                            Function = new OllamaToolCallFunction
                            {
                                Name = tc.Name,
                                Arguments = tc.Arguments.ToDictionary(
                                    kvp => kvp.Key,
                                    kvp => JsonSerializer.SerializeToElement(kvp.Value))
                            }
                        })
                        .ToList();
                }

                seeded.Add(msg);
            }

            _logger.LogInformation(
                "Loaded few-shot example '{Description}' with {TurnCount} turns",
                example.Description ?? "(unnamed)", example.Turns.Count);
        }

        return seeded;
    }

    /// <summary>
    /// Detects tool calls smuggled into the assistant's `content` field. Small models
    /// often emit JSON of the form {"name": "...", "parameters": {...}} (or `"arguments"`)
    /// instead of using Ollama's native `tool_calls`. Returns true if such a shape is found.
    /// </summary>
    private bool TryParseContentToolCall(string? content, out OllamaToolCall? call)
    {
        call = null;
        if (string.IsNullOrWhiteSpace(content)) return false;

        // Strip Markdown code fences if present (```json ... ```)
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0) trimmed = trimmed[(firstNl + 1)..];
            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) trimmed = trimmed[..fenceEnd];
            trimmed = trimmed.Trim();
        }

        // Find the first JSON object substring (the model may prefix prose).
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        var jsonSlice = trimmed[start..(end + 1)];

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(jsonSlice).RootElement;
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return false;

        var toolName = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(toolName) || _toolRegistry.Get(toolName) is null) return false;

        // Arguments may live under "parameters" or "arguments"
        Dictionary<string, JsonElement> args = new();
        if (root.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in p.EnumerateObject()) args[prop.Name] = prop.Value.Clone();
        }
        else if (root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in a.EnumerateObject()) args[prop.Name] = prop.Value.Clone();
        }

        call = new OllamaToolCall
        {
            Function = new OllamaToolCallFunction { Name = toolName!, Arguments = args }
        };
        return true;
    }

    /// <summary>
    /// Scans tool-role messages for JSON arrays of task masters and extracts id+name pairs.
    /// The product-service returns a JSON array of TaskMaster objects, each with "id" and "name".
    /// </summary>
    private List<TaskMasterMention> ExtractMentions(IEnumerable<OllamaChatMessage> messages)
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
