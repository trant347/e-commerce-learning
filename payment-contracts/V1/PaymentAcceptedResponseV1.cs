namespace Payment.Contracts.V1;

public sealed record PaymentAcceptedResponseV1
{
    public const string PendingStatus = "PENDING";

    public required Guid SagaId { get; init; }
    public required Guid EscrowId { get; init; }
    public string Status { get; init; } = PendingStatus;
    public required string StatusUrl { get; init; }
}
