using ai_assistant_service.Services.Mcp;

namespace ai_assistant_service.Services.Contracts;

/// <summary>One category decision against a captured, validated catalog.</summary>
public sealed record JevDecisionRequest(
    string UserRequest,
    IReadOnlyList<string> ClarificationHistory,
    CategoryCatalogSnapshot Catalog,
    TimeSpan RemainingTime);

/// <summary>A validated Choice answer with exactly the requested alternatives.</summary>
public sealed record JevChoiceAnswer(
    string Choice,
    double Confidence,
    IReadOnlyDictionary<string, double> Probabilities);

/// <summary>Validated Jev answers and provider diagnostic metadata.</summary>
public sealed record JevDecisionResponse(
    JevChoiceAnswer Intent,
    JevChoiceAnswer Category,
    string ResolvedModel,
    int InputTokens,
    int OutputTokens,
    double? Cost);

public interface IJevDecisionClient
{
    Task<JevDecisionResponse> DecideAsync(
        JevDecisionRequest request,
        CancellationToken cancellationToken);
}

public enum JevFailureCategory
{
    InvalidRequest,
    Unauthorized,
    InsufficientCredits,
    Forbidden,
    PayloadTooLarge,
    RateLimited,
    Timeout,
    ProviderUnavailable,
    ProtocolError
}

/// <summary>Safe provider failure metadata; raw response and decision content are not retained.</summary>
public sealed class JevDecisionException(
    JevFailureCategory category,
    int? httpStatusCode = null)
    : Exception($"Category decision failed ({category}).")
{
    public JevFailureCategory Category { get; } = category;
    public int? HttpStatusCode { get; } = httpStatusCode;
}
