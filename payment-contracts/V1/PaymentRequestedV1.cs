using System.Text.Json.Serialization;

namespace Payment.Contracts.V1;

public sealed record PaymentRequestedV1
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required Guid SagaId { get; init; }
    public required Guid EscrowId { get; init; }
    public required string BookingId { get; init; }
    public required string Operation { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required string PayerUserId { get; init; }
    public required string PayeeUserId { get; init; }
    public string? PaymentMethodToken { get; init; }

    [JsonIgnore]
    public string KafkaMessageKey => SagaId.ToString("D");

    public void Validate()
    {
        if (Operation == PaymentOperation.FundEscrow)
        {
            if (string.IsNullOrWhiteSpace(PaymentMethodToken))
            {
                throw new ArgumentException(
                    "FUND_ESCROW requires a payment-method token.",
                    nameof(PaymentMethodToken));
            }

            return;
        }

        if (Operation is PaymentOperation.ReleaseEscrow or PaymentOperation.RefundEscrow)
        {
            if (!string.IsNullOrWhiteSpace(PaymentMethodToken))
            {
                throw new ArgumentException(
                    $"{Operation} must not include a payment-method token.",
                    nameof(PaymentMethodToken));
            }

            return;
        }

        throw new ArgumentException($"Unsupported payment operation '{Operation}'.", nameof(Operation));
    }
}
