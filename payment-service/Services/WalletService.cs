using Microsoft.EntityFrameworkCore;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    public class WalletService : IWalletService
    {
        private readonly PaymentDbContext _dbContext;
        private readonly ILogger<WalletService> _logger;

        public WalletService(PaymentDbContext dbContext, ILogger<WalletService> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task<UserWallet> CreateWalletAsync(string userId, CancellationToken ct = default)
        {
            var existing = await _dbContext.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, ct);
            if (existing != null)
            {
                _logger.LogInformation("Wallet already exists for user {UserId}; ignoring duplicate creation request", userId);
                return existing;
            }

            var wallet = new UserWallet { UserId = userId };
            _dbContext.Wallets.Add(wallet);
            try
            {
                await _dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two concurrent creation requests (e.g. a retried USER_REGISTERED event) raced
                // past the earlier existence check; the primary key rejected the second insert.
                // Return the wallet the other request just committed instead of surfacing an error.
                var winner = await _dbContext.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, ct);
                if (winner != null)
                {
                    return winner;
                }
                throw;
            }

            _logger.LogInformation("Created wallet for user {UserId} with starting balance {Balance}",
                userId, wallet.Balance);
            return wallet;
        }

        public Task<UserWallet?> GetWalletAsync(string userId, CancellationToken ct = default) =>
            _dbContext.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, ct);
    }
}
