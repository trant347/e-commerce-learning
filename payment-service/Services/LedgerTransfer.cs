namespace payment_service.Services
{
    public sealed record LedgerAccountReference(
        string? OwnerUserId,
        string AccountType);

    public sealed record LedgerTransfer
    {
        public required string IdempotencyKey { get; init; }

        public Guid? PaymentTransactionId { get; init; }

        public Guid? SagaId { get; init; }

        public Guid? EscrowId { get; init; }

        public string? BookingId { get; init; }

        public required string Operation { get; init; }

        public required string Currency { get; init; }

        public decimal Amount { get; init; }

        public required LedgerAccountReference DebitAccount { get; init; }

        public required LedgerAccountReference CreditAccount { get; init; }

        public required string Description { get; init; }
    }
}
