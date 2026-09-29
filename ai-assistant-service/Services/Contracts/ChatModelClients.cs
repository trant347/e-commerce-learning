namespace ai_assistant_service.Services.Contracts;

public interface IChatModelClient
{
    Task<ChatModelResponse> ChatAsync(
        ChatModelRequest request,
        CancellationToken cancellationToken);
}

public interface ITaskChatModelResolver
{
    ITaskChatModelClient Resolve(string taskId);
}

public interface ITaskChatModelClient
{
    TaskModelProfile Profile { get; }

    Task<ChatModelResponse> ChatAsync(
        IReadOnlyList<ChatModelMessage> messages,
        IReadOnlyList<ChatModelTool>? tools,
        CancellationToken cancellationToken);
}

public sealed record TaskModelProfile
{
    public TaskModelProfile(
        string taskId,
        string provider,
        string model,
        int timeoutSeconds,
        int requestTimeoutSeconds,
        int maxOutputTokens)
    {
        TaskId = RequireValue(taskId, nameof(taskId));
        Provider = RequireValue(provider, nameof(provider));
        Model = RequireValue(model, nameof(model));

        if (timeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutSeconds),
                "The per-round timeout must be greater than zero.");
        }

        if (requestTimeoutSeconds < timeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeoutSeconds),
                "The request timeout cannot be shorter than the per-round timeout.");
        }

        if (maxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens),
                "The output token limit must be greater than zero.");
        }

        TimeoutSeconds = timeoutSeconds;
        RequestTimeoutSeconds = requestTimeoutSeconds;
        MaxOutputTokens = maxOutputTokens;
    }

    public string TaskId { get; }
    public string Provider { get; }
    public string Model { get; }
    public int TimeoutSeconds { get; }
    public int RequestTimeoutSeconds { get; }
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
