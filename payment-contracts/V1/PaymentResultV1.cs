using System.Text.Json.Serialization;

namespace Payment.Contracts.V1;

public sealed record PaymentResultV1
{
    public const int CurrentSchemaVersion = 1;
    public const string StatusApproved = "APPROVED";
    public const string StatusDeclined = "DECLINED";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required Guid SagaId { get; init; }
    public required Guid TransactionId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required string Status { get; init; }
    public string? DeclineReason { get; init; }

    [JsonIgnore]
    public string KafkaMessageKey => SagaId.ToString("D");
}
