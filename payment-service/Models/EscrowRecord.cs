using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace payment_service.Models
{
    /// <summary>
    /// Authoritative per-booking escrow ledger. The custody wallet's aggregate balance is not
    /// sufficient to determine which booking owns held funds.
    /// </summary>
    public class EscrowRecord
    {
        public const string StatusPending = Payment.Contracts.V1.EscrowStatus.Pending;
        public const string StatusFunded = Payment.Contracts.V1.EscrowStatus.Funded;
        public const string StatusReleased = Payment.Contracts.V1.EscrowStatus.Released;
        public const string StatusRefunded = Payment.Contracts.V1.EscrowStatus.Refunded;

        [Key]
        public Guid Id { get; set; }

        [MaxLength(100)]
        public string BookingId { get; set; } = string.Empty;

        [Column(TypeName = "numeric(18,2)")]
        public decimal Amount { get; set; }

        [MaxLength(3)]
        public string Currency { get; set; } = "USD";

        [MaxLength(200)]
        public string RequesterUserId { get; set; } = string.Empty;

        [MaxLength(200)]
        public string TaskMasterUserId { get; set; } = string.Empty;

        [MaxLength(200)]
        public string CustodyUserId { get; set; } = string.Empty;

        [MaxLength(20)]
        public string Status { get; set; } = StatusPending;

        public Guid? FundingTransactionId { get; set; }
        public Guid? ReleaseTransactionId { get; set; }
        public Guid? RefundTransactionId { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? FundedAt { get; set; }
        public DateTime? ReleasedAt { get; set; }
        public DateTime? RefundedAt { get; set; }
    }
}
