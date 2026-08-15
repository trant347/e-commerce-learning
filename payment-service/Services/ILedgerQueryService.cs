namespace payment_service.Services
{
    /// <summary>
    /// Reads cached and journal-authoritative ledger balances without modifying financial data.
    /// </summary>
    public interface ILedgerQueryService
    {
        /// <summary>Returns the current cached wallet projection for an account.</summary>
        Task<LedgerProjectedBalance> GetProjectedBalanceAsync(
            Guid accountId,
            CancellationToken cancellationToken = default);

        /// <summary>Calculates the current authoritative balance from all journal lines.</summary>
        Task<decimal> GetJournalBalanceAsync(
            Guid accountId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Calculates the authoritative balance using journal lines posted at or before
        /// <paramref name="asOf"/>.
        /// </summary>
        Task<decimal> GetHistoricalBalanceAsync(
            Guid accountId,
            DateTimeOffset asOf,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns one stable, ordered page of account activity. Entries are ordered by posting
        /// time, journal entry ID, and line number.
        /// </summary>
        Task<LedgerStatementPage> GetStatementAsync(
            Guid accountId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default);
    }
}
