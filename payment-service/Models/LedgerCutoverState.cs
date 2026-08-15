using System.ComponentModel.DataAnnotations;

namespace payment_service.Models
{
    public sealed class LedgerCutoverState
    {
        public const int SingletonId = 1;

        [Key]
        public int Id { get; set; } = SingletonId;

        [MaxLength(3)]
        public string Currency { get; set; } = "USD";

        public DateTime LedgerEpochAt { get; set; }

        public DateTime CompletedAt { get; set; }

        public int WalletCount { get; set; }
    }
}
