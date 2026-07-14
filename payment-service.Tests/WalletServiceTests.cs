using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    /// <summary>
    /// Covers wallet creation/lookup: every registered user gets a wallet seeded with
    /// UserWallet.DefaultStartingBalance (see UserRegisteredConsumerWorker), and creation is
    /// idempotent so a retried USER_REGISTERED event can't grant extra credit.
    /// </summary>
    public class WalletServiceTests
    {
        private static PaymentDbContext NewInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new PaymentDbContext(options);
        }

        [Fact]
        public async Task CreateWalletAsync_NewUser_CreatesWalletWithDefaultBalance()
        {
            await using var dbContext = NewInMemoryContext();
            var service = new WalletService(dbContext, NullLogger<WalletService>.Instance);

            var wallet = await service.CreateWalletAsync("alice");

            Assert.Equal("alice", wallet.UserId);
            Assert.Equal(UserWallet.DefaultStartingBalance, wallet.Balance);
            Assert.Equal(1, await dbContext.Wallets.CountAsync());
        }

        [Fact]
        public async Task CreateWalletAsync_CalledTwiceForSameUser_DoesNotGrantExtraCredit()
        {
            await using var dbContext = NewInMemoryContext();
            var service = new WalletService(dbContext, NullLogger<WalletService>.Instance);

            var first = await service.CreateWalletAsync("bob");
            var second = await service.CreateWalletAsync("bob");

            Assert.Equal(first.UserId, second.UserId);
            Assert.Equal(1, await dbContext.Wallets.CountAsync());
            Assert.Equal(UserWallet.DefaultStartingBalance, (await dbContext.Wallets.SingleAsync()).Balance);
        }

        [Fact]
        public async Task GetWalletAsync_UnknownUser_ReturnsNull()
        {
            await using var dbContext = NewInMemoryContext();
            var service = new WalletService(dbContext, NullLogger<WalletService>.Instance);

            var wallet = await service.GetWalletAsync("nobody");

            Assert.Null(wallet);
        }
    }
}
