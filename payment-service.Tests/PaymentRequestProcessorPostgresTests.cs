using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Payment.Contracts.V1;
using Xunit;

namespace payment_service.Tests
{
    public class PaymentRequestProcessorPostgresTests
    {
        [Fact]
        public async Task ProcessAsync_ConcurrentDuplicateFunding_PostsOnce()
        {
            var connectionString =
                Environment.GetEnvironmentVariable("PAYMENT_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var setup = new PaymentDbContext(options))
            {
                await setup.Database.MigrateAsync();
                await SeedWalletsAsync(setup);
            }

            var request = new PaymentRequestedV1
            {
                SagaId = Guid.NewGuid(),
                EscrowId = Guid.NewGuid(),
                BookingId = "postgres-booking",
                Operation = PaymentOperation.FundEscrow,
                Amount = 100m,
                Currency = "USD",
                PayerUserId = "postgres-requester",
                PayeeUserId = "postgres-custody",
                TaskMasterUserId = "postgres-taskmaster",
                PaymentMethodToken = "postgres-token"
            };

            var first = ProcessAsync(options, request);
            var second = ProcessAsync(options, request);
            var results = await Task.WhenAll(first, second);

            Assert.Equal(results[0], results[1]);
            await using var verification = new PaymentDbContext(options);
            Assert.Equal(1, await verification.Transactions.CountAsync());
            Assert.Equal(1, await verification.JournalEntries.CountAsync());
            Assert.Equal(2, await verification.JournalLines.CountAsync());
            Assert.Equal(1, await verification.PaymentResultOutbox.CountAsync());
            Assert.Equal(
                400m,
                (await verification.Wallets.SingleAsync(
                    wallet => wallet.UserId == "postgres-requester")).Balance);
            Assert.Equal(
                100m,
                (await verification.Wallets.SingleAsync(
                    wallet => wallet.UserId == "postgres-custody")).Balance);
        }

        private static async Task<PaymentResultV1> ProcessAsync(
            DbContextOptions<PaymentDbContext> options,
            PaymentRequestedV1 request)
        {
            await using var dbContext = new PaymentDbContext(options);
            var tokenService = new Mock<IPaymentMethodTokenService>();
            tokenService.Setup(service => service.RedeemAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RedeemedPaymentMethod(
                    "************1111",
                    "Requester",
                    false));
            var ledger = new LedgerService(
                dbContext,
                TimeProvider.System,
                NullLogger<LedgerService>.Instance);
            var processor = new PaymentRequestProcessor(
                dbContext,
                tokenService.Object,
                ledger,
                TimeProvider.System,
                NullLogger<PaymentRequestProcessor>.Instance);
            return await processor.ProcessAsync(request);
        }

        private static async Task SeedWalletsAsync(PaymentDbContext dbContext)
        {
            await AddWalletAsync(
                dbContext,
                "postgres-requester",
                LedgerAccount.TypeUserWallet,
                500m);
            await AddWalletAsync(
                dbContext,
                "postgres-custody",
                LedgerAccount.TypeEscrowCustody,
                0m);
            await AddWalletAsync(
                dbContext,
                "postgres-taskmaster",
                LedgerAccount.TypeUserWallet,
                0m);
            await dbContext.SaveChangesAsync();
        }

        private static Task AddWalletAsync(
            PaymentDbContext dbContext,
            string userId,
            string accountType,
            decimal balance)
        {
            var account = new LedgerAccount
            {
                OwnerUserId = userId,
                AccountType = accountType,
                Currency = "USD"
            };
            dbContext.LedgerAccounts.Add(account);
            dbContext.Wallets.Add(new UserWallet
            {
                UserId = userId,
                Balance = balance,
                LedgerAccountId = account.Id
            });
            return Task.CompletedTask;
        }
    }
}
