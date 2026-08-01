namespace Payment.Contracts.V1;

public sealed record PaymentStatusResponseV1
{
    public const string PendingStatus = "PENDING";

    public required Guid SagaId { get; init; }
    public required string BookingId { get; init; }
    public required Guid EscrowId { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public string? EscrowStatus { get; init; }
    public string? FailureReason { get; init; }
    public DateTime UpdatedAt { get; init; }
}
