using System.Text.Json.Serialization;

namespace Payment.Contracts.V1;

public sealed record PaymentRequestedV1
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required Guid SagaId { get; init; }
    public required string BookingId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required string PayerUserId { get; init; }
    public required string PayeeUserId { get; init; }
    public required string PaymentMethodToken { get; init; }

    [JsonIgnore]
    public string KafkaMessageKey => SagaId.ToString("D");
}
