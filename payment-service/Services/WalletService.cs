using Microsoft.EntityFrameworkCore;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    public class WalletService : IWalletService
    {
        private readonly PaymentDbContext _dbContext;
        private readonly ILedgerAccountService _ledgerAccounts;
        private readonly ILogger<WalletService> _logger;

        public WalletService(
            PaymentDbContext dbContext,
            ILedgerAccountService ledgerAccounts,
            ILogger<WalletService> logger)
        {
            _dbContext = dbContext;
            _ledgerAccounts = ledgerAccounts;
            _logger = logger;
        }

        public async Task<UserWallet> CreateWalletAsync(string userId, CancellationToken ct = default)
        {
            await _ledgerAccounts.EnsureUserWalletAccountAsync(userId, cancellationToken: ct);
            var wallet = await _dbContext.Wallets.SingleAsync(
                candidate => candidate.UserId == userId,
                ct);

            _logger.LogInformation("Wallet ensured for user {UserId} with balance {Balance}",
                userId, wallet.Balance);
            return wallet;
        }

        public Task<UserWallet?> GetWalletAsync(string userId, CancellationToken ct = default) =>
            _dbContext.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, ct);
    }
}
