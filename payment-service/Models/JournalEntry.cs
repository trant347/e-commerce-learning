using System.ComponentModel.DataAnnotations;

namespace payment_service.Models
{
    public class JournalEntry
    {
        public const string OperationOpeningBalance = "OPENING_BALANCE";
        public const string OperationUserRegistrationCredit = "USER_REGISTRATION_CREDIT";
        public const string OperationLegacyPayment = "LEGACY_PAYMENT";
        public const string OperationFundEscrow = "FUND_ESCROW";
        public const string OperationReleaseEscrow = "RELEASE_ESCROW";
        public const string OperationRefundEscrow = "REFUND_ESCROW";
        public const string OperationReversal = "REVERSAL";
        public const string OperationAdminAdjustment = "ADMIN_ADJUSTMENT";

        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [MaxLength(200)]
        public string IdempotencyKey { get; set; } = string.Empty;

        public Guid? PaymentTransactionId { get; set; }

        public Guid? SagaId { get; set; }

        public Guid? EscrowId { get; set; }

        [MaxLength(100)]
        public string? BookingId { get; set; }

        [MaxLength(30)]
        public string Operation { get; set; } = string.Empty;

        [MaxLength(3)]
        public string Currency { get; set; } = "USD";

        [MaxLength(500)]
        public string Description { get; set; } = string.Empty;

        public Guid? ReversesJournalEntryId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public PaymentTransaction? PaymentTransaction { get; set; }

        public JournalEntry? ReversesJournalEntry { get; set; }

        public JournalEntry? ReversalJournalEntry { get; set; }

        public ICollection<JournalLine> Lines { get; set; } = [];
    }
}
