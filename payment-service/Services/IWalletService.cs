using payment_service.Models;

namespace payment_service.Services
{
    /// <summary>
    /// Manages per-user simulated wallets (see UserWallet). Distinct from IPaymentGateway:
    /// this is the "account administration" side (creating a wallet, checking a balance),
    /// while the gateway is the "charge decision" side used mid-transaction by PaymentService.
    /// </summary>
    public interface IWalletService
    {
        /// <summary>
        /// Creates a wallet seeded with <see cref="UserWallet.DefaultStartingBalance"/> for the
        /// given user if one doesn't already exist. Idempotent: safe to call more than once for
        /// the same userId (e.g. a retried USER_REGISTERED event) without granting extra credit.
        /// </summary>
        Task<UserWallet> CreateWalletAsync(string userId, CancellationToken ct = default);

        /// <summary>Looks up a user's wallet, or null if they don't have one yet.</summary>
        Task<UserWallet?> GetWalletAsync(string userId, CancellationToken ct = default);
    }
}
