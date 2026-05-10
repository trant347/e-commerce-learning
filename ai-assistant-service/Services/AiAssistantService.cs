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

        // Seed the conversation
        var messages = new List<OllamaChatMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user",   Content = request.Message }
        };

        // Tool-calling loop
        for (int round = 0; round < MaxToolRounds; round++)
        {
            _logger.LogInformation("Starting tool-calling round {Round}/{MaxRounds}, message count={MessageCount}", 
                round + 1, MaxToolRounds, messages.Count);

            var assistantMsg = await _ollamaClient.ChatAsync(model, messages, toolDefinitions, cancellationToken);
            messages.Add(assistantMsg);

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

                var result = await _toolRegistry.ExecuteAsync(toolName, args, cancellationToken);

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
        var finalContent = messages
            .LastOrDefault(m => m.Role == "assistant"
                             && m.ToolCalls is not { Count: > 0 }
                             && !string.IsNullOrWhiteSpace(m.Content))
            ?.Content
            ?? "I wasn't able to find a complete answer — the data may be unavailable or the request needs more detail. Please try rephrasing.";

        _logger.LogInformation("Chat request completed with final answer length={AnswerLength}, total messages={MessageCount}", 
            finalContent.Length, messages.Count);

        return new ChatResponse
        {
            Answer  = finalContent,
            Model   = model,
            Sources = _toolRegistry.All.Select(t => t.Name).ToList()
        };
    }
}
