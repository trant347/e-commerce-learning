using System.ComponentModel.DataAnnotations;

namespace payment_service.Models
{
    public class LedgerAccount
    {
        public const string TypeUserWallet = "USER_WALLET";
        public const string TypeEscrowCustody = "ESCROW_CUSTODY";
        public const string TypeSystemIssuance = "SYSTEM_ISSUANCE";

        public const string StatusActive = "ACTIVE";
        public const string StatusClosed = "CLOSED";

        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [MaxLength(200)]
        public string? OwnerUserId { get; set; }

        [MaxLength(30)]
        public string AccountType { get; set; } = string.Empty;

        [MaxLength(3)]
        public string Currency { get; set; } = "USD";

        [MaxLength(20)]
        public string Status { get; set; } = StatusActive;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? ClosedAt { get; set; }

        public ICollection<JournalLine> JournalLines { get; set; } = [];
    }
}
