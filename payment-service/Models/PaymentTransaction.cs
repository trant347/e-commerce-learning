using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace payment_service.Models
{
    /// <summary>
    /// A persisted record of a payment attempt, including its outcome. Card numbers are
    /// never stored in full — only a masked representation is kept for auditing.
    /// </summary>
    public class PaymentTransaction
    {
        public const string StatusApproved = "APPROVED";
        public const string StatusDeclined = "DECLINED";

        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Stored as PostgreSQL `numeric` (arbitrary precision, exact decimal) — never
        /// `float`/`double` — so no rounding errors can creep into monetary amounts.
        /// </summary>
        [Column(TypeName = "numeric(18,2)")]
        public decimal Amount { get; set; }

        [MaxLength(3)]
        public string Currency { get; set; } = "USD";

        [MaxLength(32)]
        public string MaskedCardNumber { get; set; } = string.Empty;

        [MaxLength(200)]
        public string OwnerName { get; set; } = string.Empty;

        [MaxLength(20)]
        public string Status { get; set; } = StatusApproved;

        /// <summary>
        /// Human-readable reason for a decline (e.g. "Insufficient balance"), surfaced to the
        /// caller so it can show a clearer error than a generic "Payment was declined". Null
        /// for approved transactions.
        /// </summary>
        [MaxLength(200)]
        public string? DeclineReason { get; set; }

        /// <summary>
        /// Idempotency key from the caller's saga, if supplied. Unique when present so a
        /// retried request with the same SagaId can be detected and deduped instead of
        /// double-charging (see PAYMENT_SAGA_SPEC.md, migration step 2).
        /// </summary>
        public Guid? SagaId { get; set; }

        public Guid? EscrowId { get; set; }

        [MaxLength(100)]
        public string? BookingId { get; set; }

        [MaxLength(20)]
        public string? Operation { get; set; }

        [MaxLength(200)]
        public string? PayerUserId { get; set; }

        [MaxLength(200)]
        public string? PayeeUserId { get; set; }

        [MaxLength(200)]
        public string? TaskMasterUserId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
