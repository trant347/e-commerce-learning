using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Services;

/// <summary>
/// Snapshots the prompt and validated example conversations as shared messages.
/// Pairing is checked without a tool registry because MCP discovery can finish
/// after startup; examples describe calls but never execute them.
/// </summary>
public sealed class AssistantPrompt
{
    public AssistantPrompt(IOptions<PromptOptions> options)
    {
        Messages = CreateMessages(options.Value);
    }

    public IReadOnlyList<ChatModelMessage> Messages { get; }

    internal static IReadOnlyList<ChatModelMessage> CreateMessages(PromptOptions options)
    {
        var messages = new List<ChatModelMessage>
        {
            new(ChatModelRoles.System, options.SystemPrompt)
        };
        ArgumentNullException.ThrowIfNull(options.FewShotExamples);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (var exampleIndex = 0; exampleIndex < options.FewShotExamples.Count; exampleIndex++)
        {
            var example = options.FewShotExamples[exampleIndex];
            var path = $"PromptOptions:FewShotExamples:{exampleIndex}";
            if (example?.Turns is not { Count: > 0 })
                throw new ArgumentException($"{path} must contain turns.");

            var pendingIds = new HashSet<string>(StringComparer.Ordinal);
            for (var turnIndex = 0; turnIndex < example.Turns.Count; turnIndex++)
            {
                var turnPath = $"{path}:Turns:{turnIndex}";
                var turn = example.Turns[turnIndex];
                if (turn is null || turn.Role is not ("user" or "assistant" or "tool"))
                    throw new ArgumentException($"{turnPath} has an invalid role.");
                if (turn.Role != ChatModelRoles.Tool && pendingIds.Count > 0)
                    throw new ArgumentException($"{turnPath} starts before all tool calls have results.");

                ChatModelMessage message;
                try
                {
                    var calls = turn.ToolCalls?.Select(call =>
                    {
                        ArgumentNullException.ThrowIfNull(call);
                        ArgumentNullException.ThrowIfNull(call.Arguments);
                        return new ChatModelToolCall(call.Id, call.Name, call.Arguments.ToDictionary(
                            pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)));
                    }).ToArray();
                    message = new ChatModelMessage(turn.Role, turn.Content, calls, turn.ToolCallId);
                }
                catch (ArgumentException)
                {
                    throw new ArgumentException($"{turnPath} has invalid message or tool-call fields.");
                }

                foreach (var call in message.ToolCalls ?? [])
                {
                    if (!seenIds.Add(call.Id))
                        throw new ArgumentException($"{turnPath} repeats a tool-call ID.");
                    pendingIds.Add(call.Id);
                }

                if (message.Role == ChatModelRoles.Tool && !pendingIds.Remove(message.ToolCallId!))
                    throw new ArgumentException($"{turnPath} does not resolve a preceding, unresolved tool call.");

                messages.Add(message);
            }

            if (pendingIds.Count > 0)
                throw new ArgumentException($"{path} ends with unresolved tool calls.");
        }

        return messages.AsReadOnly();
    }
}
