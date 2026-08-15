using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Data;
using payment_service.MessageQueue;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    /// <summary>
    /// Covers the "user-events" payload handling that creates a wallet for a newly registered
    /// user (see UserRegisteredConsumerWorker / UserRegisteredEventHandler). Exercises the
    /// parsing/dispatch logic directly, without needing a real Kafka broker.
    /// </summary>
    public class UserRegisteredEventHandlerTests
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
        public async Task HandleAsync_UserRegisteredEvent_CreatesWalletForUsername()
        {
            await using var dbContext = NewInMemoryContext();
            var wallets = NewWalletService(dbContext);
            var payload = "{\"type\":\"USER_REGISTERED\",\"username\":\"carol\"}";

            await UserRegisteredEventHandler.HandleAsync(payload, wallets, NullLogger.Instance);

            var wallet = await dbContext.Wallets.SingleOrDefaultAsync(w => w.UserId == "carol");
            Assert.NotNull(wallet);
            Assert.Equal(UserWallet.DefaultStartingBalance, wallet!.Balance);
        }

        [Fact]
        public async Task HandleAsync_OtherEventType_IsIgnored()
        {
            await using var dbContext = NewInMemoryContext();
            var wallets = NewWalletService(dbContext);
            var payload = "{\"type\":\"USER_DELETED\",\"username\":\"dave\"}";

            await UserRegisteredEventHandler.HandleAsync(payload, wallets, NullLogger.Instance);

            Assert.Equal(0, await dbContext.Wallets.CountAsync());
        }

        [Fact]
        public async Task HandleAsync_MissingUsername_IsIgnored()
        {
            await using var dbContext = NewInMemoryContext();
            var wallets = NewWalletService(dbContext);
            var payload = "{\"type\":\"USER_REGISTERED\"}";

            await UserRegisteredEventHandler.HandleAsync(payload, wallets, NullLogger.Instance);

            Assert.Equal(0, await dbContext.Wallets.CountAsync());
        }

        private static WalletService NewWalletService(PaymentDbContext dbContext)
        {
            var ledgerAccounts = new LedgerAccountService(
                dbContext,
                TimeProvider.System,
                NullLogger<LedgerAccountService>.Instance);
            return new WalletService(
                dbContext,
                ledgerAccounts,
                NullLogger<WalletService>.Instance);
        }
    }
}
