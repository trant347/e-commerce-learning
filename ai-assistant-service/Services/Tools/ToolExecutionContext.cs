namespace ai_assistant_service.Services.Tools;

/// <summary>
/// Trusted metadata for one tool execution. This data is created by application
/// code and is never populated from model-provided tool arguments.
/// </summary>
public sealed class ToolExecutionContext
{
    public ToolExecutionContext(
        string actor,
        IEnumerable<string> authorizedScopes,
        string correlationId,
        string? agentRunId = null,
        string? actionId = null)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("A tool execution actor is required.", nameof(actor));
        }

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("A tool execution correlation ID is required.", nameof(correlationId));
        }

        ArgumentNullException.ThrowIfNull(authorizedScopes);

        Actor = actor;
        AuthorizedScopes = Array.AsReadOnly(
            authorizedScopes
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Select(scope => scope.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        CorrelationId = correlationId;
        AgentRunId = NormalizeOptionalId(agentRunId);
        ActionId = NormalizeOptionalId(actionId);
    }

    public string Actor { get; }

    public IReadOnlyList<string> AuthorizedScopes { get; }

    public string CorrelationId { get; }

    public string? AgentRunId { get; }

    public string? ActionId { get; }

    private static string? NormalizeOptionalId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
