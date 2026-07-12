using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace calendar_service.Model
{
    /// <summary>
    /// Durable record of a booking-payment saga attempt. Written BEFORE calendar-service calls
    /// out to payment-service, so a crash mid-flight (after payment-service approves a charge
    /// but before the booking is marked COMPLETED) leaves a recoverable trail instead of a
    /// silent inconsistency. See PAYMENT_SAGA_SPEC.md, "Recommended design".
    /// </summary>
    public class SagaState
    {
        public const string StatusStarted = "STARTED";
        public const string StatusCompleted = "COMPLETED";
        public const string StatusFailed = "FAILED";

        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        /// <summary>
        /// Idempotency key shared with payment-service on the process-payment call, so a
        /// retried request (from the reconciliation job or a client retry) can't double-charge.
        /// See <see cref="calendar_service.MongoDbGuidSupport"/> (registered at startup) for why
        /// Guid serialization needs an explicit representation with MongoDB.Driver 3.x.
        /// </summary>
        public Guid SagaId { get; set; } = Guid.NewGuid();

        public string BookingId { get; set; } = string.Empty;

        public string Status { get; set; } = StatusStarted;

        /// <summary>Amount that was requested to be charged when the saga was started.</summary>
        public decimal RequestedAmount { get; set; }

        /// <summary>Set when the saga resolves to FAILED (declined, mismatch, unreachable, etc).</summary>
        public string? FailureReason { get; set; }

        /// <summary>Id of the resulting payment-service transaction, once known.</summary>
        public string? PaymentTransactionId { get; set; }

        /// <summary>
        /// Set (and refreshed) when a reconciliation worker instance claims this saga to work
        /// on, so that multiple calendar-service replicas each running their own reconciliation
        /// timer don't duplicate work (or redundantly hit payment-service) for the same stuck
        /// saga. A claim older than the worker's claim TTL is treated as stale/abandoned (e.g.
        /// the claiming replica crashed) and can be re-claimed. See
        /// <see cref="ISagaStateService.TryClaimAsync"/> and PAYMENT_SAGA_SPEC.md.
        /// </summary>
        public DateTime? ReconciliationClaimedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
