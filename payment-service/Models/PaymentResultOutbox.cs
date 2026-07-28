using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace payment_service.Models
{
    public sealed class PaymentResultOutbox
    {
        public const string StatusPending = "PENDING";
        public const string StatusClaimed = "CLAIMED";
        public const string StatusDispatched = "DISPATCHED";

        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid SagaId { get; set; }

        public Guid TransactionId { get; set; }

        [Column(TypeName = "jsonb")]
        public string Payload { get; set; } = string.Empty;

        [MaxLength(20)]
        public string DispatchStatus { get; set; } = StatusPending;

        public int DispatchAttemptCount { get; set; }

        public DateTime NextDispatchAttemptAt { get; set; } = DateTime.UtcNow;

        public DateTime? DispatchClaimedAt { get; set; }

        public DateTime? DispatchClaimExpiresAt { get; set; }

        public DateTime? DispatchedAt { get; set; }

        [MaxLength(1000)]
        public string? LastDispatchError { get; set; }

        [MaxLength(200)]
        public string? TraceParent { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
