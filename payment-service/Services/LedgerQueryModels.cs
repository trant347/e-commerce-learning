namespace payment_service.Services
{
    public sealed record LedgerProjectedBalance(
        Guid AccountId,
        decimal Balance,
        long ProjectionVersion,
        Guid? LastJournalEntryId);

    public sealed record LedgerStatementItem(
        Guid JournalEntryId,
        short LineNumber,
        DateTime CreatedAt,
        string Direction,
        decimal Amount,
        decimal SignedAmount,
        string Operation,
        string Currency,
        string Description,
        Guid? PaymentTransactionId,
        Guid? SagaId,
        Guid? EscrowId,
        string? BookingId);

    public sealed record LedgerStatementPage(
        IReadOnlyList<LedgerStatementItem> Items,
        int PageNumber,
        int PageSize,
        bool HasMore);
}
