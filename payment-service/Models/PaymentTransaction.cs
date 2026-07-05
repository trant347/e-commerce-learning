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

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
