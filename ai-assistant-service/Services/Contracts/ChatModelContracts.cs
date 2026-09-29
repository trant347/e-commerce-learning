using System.Collections.ObjectModel;
using System.Text.Json;

namespace ai_assistant_service.Services.Contracts;

public static class ChatModelRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";

    internal static bool IsSupported(string role) =>
        role is System or User or Assistant or Tool;
}

public sealed record ChatModelRequest
{
    public ChatModelRequest(
        string model,
        IReadOnlyList<ChatModelMessage> messages,
        IReadOnlyList<ChatModelTool>? tools,
        int maxOutputTokens)
    {
        Model = RequireValue(model, nameof(model));
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one message is required.", nameof(messages));
        }

        if (maxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens),
                "The output token limit must be greater than zero.");
        }

        Messages = Array.AsReadOnly(messages.ToArray());
        Tools = tools is null ? null : Array.AsReadOnly(tools.ToArray());
        MaxOutputTokens = maxOutputTokens;
    }

    public string Model { get; }
    public IReadOnlyList<ChatModelMessage> Messages { get; }
    public IReadOnlyList<ChatModelTool>? Tools { get; }
    public int MaxOutputTokens { get; }

    private static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }
}

public sealed record ChatModelTool
{
    public ChatModelTool(string name, string description, JsonElement parameters)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A tool name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A tool description is required.", nameof(description));
        }

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Tool parameters must be a JSON Schema object.",
                nameof(parameters));
        }

        Name = name.Trim();
        Description = description.Trim();
        Parameters = parameters.Clone();
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement Parameters { get; }
}

public sealed record ChatModelMessage
{
    public ChatModelMessage(
        string role,
        string? content,
        IReadOnlyList<ChatModelToolCall>? toolCalls = null,
        string? toolCallId = null)
    {
        if (string.IsNullOrWhiteSpace(role) || !ChatModelRoles.IsSupported(role))
        {
            throw new ArgumentException("The message role is not supported.", nameof(role));
        }

        var calls = toolCalls?.ToArray();
        var hasCalls = calls is { Length: > 0 };
        var hasToolCallId = !string.IsNullOrWhiteSpace(toolCallId);

        switch (role)
        {
            case ChatModelRoles.Assistant:
                if (hasToolCallId)
                {
                    throw new ArgumentException(
                        "Only tool-result messages can reference a tool-call ID.",
                        nameof(toolCallId));
                }

                if (!hasCalls && string.IsNullOrWhiteSpace(content))
                {
                    throw new ArgumentException(
                        "An assistant message requires content or at least one tool call.",
                        nameof(content));
                }
                break;

            case ChatModelRoles.Tool:
                if (!hasToolCallId)
                {
                    throw new ArgumentException(
                        "A tool-result message requires its originating tool-call ID.",
                        nameof(toolCallId));
                }

                if (hasCalls)
                {
                    throw new ArgumentException(
                        "A tool-result message cannot contain tool calls.",
                        nameof(toolCalls));
                }

                if (content is null)
                {
                    throw new ArgumentNullException(
                        nameof(content),
                        "A tool-result message requires content.");
                }
                break;

            default:
                if (hasCalls)
                {
                    throw new ArgumentException(
                        "Only assistant messages can contain tool calls.",
                        nameof(toolCalls));
                }

                if (hasToolCallId)
                {
                    throw new ArgumentException(
                        "Only tool-result messages can reference a tool-call ID.",
                        nameof(toolCallId));
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    throw new ArgumentException(
                        "System and user messages require content.",
                        nameof(content));
                }
                break;
        }

        Role = role;
        Content = content;
        ToolCalls = hasCalls ? Array.AsReadOnly(calls!) : null;
        ToolCallId = hasToolCallId ? toolCallId!.Trim() : null;
    }

    public string Role { get; }
    public string? Content { get; }
    public IReadOnlyList<ChatModelToolCall>? ToolCalls { get; }
    public string? ToolCallId { get; }
}

public sealed record ChatModelToolCall
{
    public ChatModelToolCall(string id, string name, JsonElement arguments)
        : this(id, name, ReadArguments(arguments))
    {
    }

    public ChatModelToolCall(
        string id,
        string name,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A tool-call ID is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A tool name is required.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(arguments);

        Id = id.Trim();
        Name = name.Trim();
        Arguments = CloneArguments(arguments);
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyDictionary<string, JsonElement> Arguments { get; }

    private static IReadOnlyDictionary<string, JsonElement> ReadArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Tool-call arguments must be a JSON object.",
                nameof(arguments));
        }

        return arguments
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, JsonElement> CloneArguments(
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var cloned = new Dictionary<string, JsonElement>(
            arguments.Count,
            StringComparer.Ordinal);

        foreach (var pair in arguments)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new ArgumentException(
                    "Tool-call argument names cannot be empty.",
                    nameof(arguments));
            }

            cloned.Add(pair.Key, pair.Value.Clone());
        }

        return new ReadOnlyDictionary<string, JsonElement>(cloned);
    }
}

public sealed record ChatModelResponse
{
    public ChatModelResponse(
        ChatModelMessage message,
        string? providerRequestId = null,
        string? resolvedModel = null,
        ChatModelUsage? usage = null)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        ProviderRequestId = NormalizeOptional(providerRequestId);
        ResolvedModel = NormalizeOptional(resolvedModel);
        Usage = usage;
    }

    public ChatModelMessage Message { get; }
    public string? ProviderRequestId { get; }
    public string? ResolvedModel { get; }
    public ChatModelUsage? Usage { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ChatModelUsage
{
    public ChatModelUsage(long? inputTokens, long? outputTokens, long? totalTokens)
    {
        InputTokens = RequireNonNegative(inputTokens, nameof(inputTokens));
        OutputTokens = RequireNonNegative(outputTokens, nameof(outputTokens));
        TotalTokens = RequireNonNegative(totalTokens, nameof(totalTokens));
    }

    public long? InputTokens { get; }
    public long? OutputTokens { get; }
    public long? TotalTokens { get; }

    private static long? RequireNonNegative(long? value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Token usage cannot be negative.");
        }

        return value;
    }
}
