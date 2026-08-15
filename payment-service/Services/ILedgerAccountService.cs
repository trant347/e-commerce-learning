using payment_service.Models;

namespace payment_service.Services
{
    /// <summary>
    /// Creates and retrieves the ledger accounts used by wallet and escrow flows.
    /// Account creation is idempotent so retried events and concurrent service replicas do not
    /// create duplicate accounts or issue a user's simulated starting balance more than once.
    /// </summary>
    public interface ILedgerAccountService
    {
        /// <summary>
        /// Ensures the user has an active wallet ledger account for the requested currency.
        /// A newly created wallet receives one balanced simulated starting-balance journal entry;
        /// repeated calls return the existing account without issuing additional funds.
        /// </summary>
        /// <param name="userId">The unique user identifier that owns the wallet.</param>
        /// <param name="currency">The three-letter currency code for the account.</param>
        /// <param name="cancellationToken">Cancels the asynchronous operation.</param>
        /// <returns>The existing or newly created user wallet ledger account.</returns>
        Task<LedgerAccount> EnsureUserWalletAccountAsync(
            string userId,
            string currency = "USD",
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Ensures the configured escrow custodian has an active zero-balance ledger account and
        /// linked wallet projection. This operation never issues simulated funds.
        /// </summary>
        /// <param name="custodyUserId">The configured identifier for the escrow custodian.</param>
        /// <param name="currency">The three-letter currency code for the account.</param>
        /// <param name="cancellationToken">Cancels the asynchronous operation.</param>
        /// <returns>The existing or newly created escrow custody ledger account.</returns>
        Task<LedgerAccount> EnsureCustodyAccountAsync(
            string custodyUserId,
            string currency = "USD",
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Ensures the simulation-only issuance account exists for the requested currency.
        /// This account provides the balancing debit for artificial user starting balances and
        /// does not have a wallet projection.
        /// </summary>
        /// <param name="currency">The three-letter currency code for the account.</param>
        /// <param name="cancellationToken">Cancels the asynchronous operation.</param>
        /// <returns>The single system issuance ledger account for the currency.</returns>
        Task<LedgerAccount> EnsureSystemIssuanceAccountAsync(
            string currency = "USD",
            CancellationToken cancellationToken = default);
    }
}
