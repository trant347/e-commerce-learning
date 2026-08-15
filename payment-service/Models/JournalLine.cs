using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace payment_service.Models
{
    public class JournalLine
    {
        public const string DirectionDebit = "DEBIT";
        public const string DirectionCredit = "CREDIT";

        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid JournalEntryId { get; set; }

        public short LineNumber { get; set; }

        public Guid AccountId { get; set; }

        [MaxLength(6)]
        public string Direction { get; set; } = string.Empty;

        [Column(TypeName = "numeric(18,2)")]
        public decimal Amount { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public JournalEntry JournalEntry { get; set; } = null!;

        public LedgerAccount Account { get; set; } = null!;
    }
}
