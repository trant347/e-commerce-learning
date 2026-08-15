using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using payment_service.Contracts;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class PaymentServicePostgresTests
    {
        [Fact]
        public async Task ProcessPaymentAsync_ConcurrentSaga_PostsLegacyTransferOnce()
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
                await SeedAsync(setup);
            }

            var sagaId = Guid.NewGuid();
            var first = ProcessAsync(options, Request(sagaId));
            var second = ProcessAsync(options, Request(sagaId));
            var results = await Task.WhenAll(first, second);

            Assert.Equal(results[0].Id, results[1].Id);
            await using var verification = new PaymentDbContext(options);
            Assert.Equal(1, await verification.Transactions.CountAsync());
            Assert.Equal(
                1,
                await verification.JournalEntries.CountAsync(entry =>
                    entry.Operation == JournalEntry.OperationLegacyPayment));
            Assert.Equal(
                900m,
                (await verification.Wallets.SingleAsync(
                    wallet => wallet.UserId == "legacy-payer")).Balance);
            Assert.Equal(
                1100m,
                (await verification.Wallets.SingleAsync(
                    wallet => wallet.UserId == "legacy-payee")).Balance);
        }

        private static async Task<PaymentTransaction> ProcessAsync(
            DbContextOptions<PaymentDbContext> options,
            PaymentRequest request)
        {
            await using var dbContext = new PaymentDbContext(options);
            var ledgerAccounts = new LedgerAccountService(
                dbContext,
                TimeProvider.System,
                NullLogger<LedgerAccountService>.Instance);
            var ledger = new LedgerService(
                dbContext,
                TimeProvider.System,
                NullLogger<LedgerService>.Instance);
            var gateway = new WalletSimulationPaymentGateway(
                ledgerAccounts,
                ledger,
                Options.Create(new LegacyPaymentOptions()),
                NullLogger<WalletSimulationPaymentGateway>.Instance);
            var service = new PaymentService(
                dbContext,
                gateway,
                NullLogger<PaymentService>.Instance);
            return await service.ProcessPaymentAsync(request);
        }

        private static PaymentRequest Request(Guid sagaId) => new()
        {
            CreditCard = new CreditCardInfo
            {
                CardNumber = "4111111111111111",
                ExpiryDate = "12/30",
                CVV = "123",
                OwnerName = "Legacy payer"
            },
            Amount = 100m,
            Currency = "USD",
            PayerUserId = "legacy-payer",
            PayeeUserId = "legacy-payee",
            SagaId = sagaId
        };

        private static async Task SeedAsync(PaymentDbContext dbContext)
        {
            var payer = new LedgerAccount
            {
                OwnerUserId = "legacy-payer",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            var payee = new LedgerAccount
            {
                OwnerUserId = "legacy-payee",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            dbContext.LedgerAccounts.AddRange(payer, payee);
            dbContext.Wallets.AddRange(
                new UserWallet
                {
                    UserId = "legacy-payer",
                    Balance = 1000m,
                    LedgerAccountId = payer.Id
                },
                new UserWallet
                {
                    UserId = "legacy-payee",
                    Balance = 1000m,
                    LedgerAccountId = payee.Id
                });
            await dbContext.SaveChangesAsync();
        }
    }
}
