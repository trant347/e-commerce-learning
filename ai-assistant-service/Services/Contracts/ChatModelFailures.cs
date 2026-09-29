namespace ai_assistant_service.Services.Contracts;

public enum ChatModelFailureCategory
{
    InvalidConfiguration,
    InvalidRequest,
    LocalModelUnavailable,
    Authentication,
    CreditsExhausted,
    TemporaryInFlightBudget,
    PolicyRejected,
    Timeout,
    RateLimited,
    ProviderUnavailable,
    ProtocolError,
    CallerCancellation,
    UnknownProviderError
}

public sealed record ChatModelFailureMetadata
{
    public ChatModelFailureMetadata(
        string? provider = null,
        string? requestedModel = null,
        string? resolvedModel = null,
        int? httpStatusCode = null,
        int? providerErrorCode = null,
        string? providerRequestId = null,
        TimeSpan? retryAfter = null,
        string? limitSource = null)
    {
        if (httpStatusCode is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(httpStatusCode),
                "An HTTP status code must be positive.");
        }

        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryAfter),
                "A retry delay cannot be negative.");
        }

        Provider = NormalizeOptional(provider);
        RequestedModel = NormalizeOptional(requestedModel);
        ResolvedModel = NormalizeOptional(resolvedModel);
        HttpStatusCode = httpStatusCode;
        ProviderErrorCode = providerErrorCode;
        ProviderRequestId = NormalizeOptional(providerRequestId);
        RetryAfter = retryAfter;
        LimitSource = NormalizeOptional(limitSource);
    }

    public string? Provider { get; }
    public string? RequestedModel { get; }
    public string? ResolvedModel { get; }
    public int? HttpStatusCode { get; }
    public int? ProviderErrorCode { get; }
    public string? ProviderRequestId { get; }
    public TimeSpan? RetryAfter { get; }
    public string? LimitSource { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ChatModelException : Exception
{
    public ChatModelException(
        ChatModelFailureCategory category,
        ChatModelFailureMetadata? metadata = null)
        : base($"Chat model request failed with category '{category}'.")
    {
        Category = category;
        Metadata = metadata ?? new ChatModelFailureMetadata();
    }

    public ChatModelFailureCategory Category { get; }
    public ChatModelFailureMetadata Metadata { get; }
}
