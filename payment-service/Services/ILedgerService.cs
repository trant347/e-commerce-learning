namespace payment_service.Services
{
    /// <summary>
    /// Posts immutable, balanced transfers and updates linked wallet projections atomically.
    /// </summary>
    public interface ILedgerService
    {
        /// <summary>
        /// Posts one debit and one credit for the requested transfer. Reusing an idempotency key
        /// with identical terms returns the original posting; conflicting reuse is rejected.
        /// The method joins an existing database transaction and does not commit that transaction.
        /// </summary>
        Task<LedgerPostingResult> PostTransferAsync(
            LedgerTransfer transfer,
            CancellationToken cancellationToken = default);
    }
}
