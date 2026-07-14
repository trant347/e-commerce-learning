using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace payment_service.Models
{
    /// <summary>
    /// A simulated per-user balance used to make payment declines realistic (insufficient
    /// funds) instead of relying solely on a scripted "magic" test card number. Created with
    /// a starting balance when authorization-service publishes USER_REGISTERED (see
    /// PAYMENT_SAGA_SPEC.md and UserRegisteredConsumerWorker).
    /// </summary>
    public class UserWallet
    {
        /// <summary>Starting balance granted to every newly registered user.</summary>
        public const decimal DefaultStartingBalance = 1000m;

        /// <summary>
        /// The owning user's username, as issued by authorization-service. Not a surrogate id —
        /// usernames are already unique and are what booking/payment requests carry.
        /// </summary>
        [Key]
        [MaxLength(200)]
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Stored as PostgreSQL `numeric` (arbitrary precision, exact decimal) — never
        /// `float`/`double` — so no rounding errors can creep into monetary amounts.
        /// </summary>
        [Column(TypeName = "numeric(18,2)")]
        public decimal Balance { get; set; } = DefaultStartingBalance;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
