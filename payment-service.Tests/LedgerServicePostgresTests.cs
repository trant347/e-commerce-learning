using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class LedgerServicePostgresTests
    {
        [Fact]
        public async Task PostTransferAsync_UsesPostgresLocksAndCallerTransaction()
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
            await using var dbContext = new PaymentDbContext(options);
            await dbContext.Database.MigrateAsync();
            await SeedAsync(dbContext);
            var service = new LedgerService(
                dbContext,
                TimeProvider.System,
                NullLogger<LedgerService>.Instance);
            var transfer = Transfer();

            await using (var transaction =
                         await dbContext.Database.BeginTransactionAsync())
            {
                await service.PostTransferAsync(transfer);
                await transaction.RollbackAsync();
            }

            dbContext.ChangeTracker.Clear();
            Assert.Equal(0, await dbContext.JournalEntries.CountAsync());
            Assert.Equal(
                100m,
                (await dbContext.Wallets.SingleAsync(
                    wallet => wallet.UserId == "postgres-payer")).Balance);

            var first = await service.PostTransferAsync(transfer);
            var duplicate = await service.PostTransferAsync(transfer);

            Assert.False(first.WasAlreadyPosted);
            Assert.True(duplicate.WasAlreadyPosted);
            Assert.Equal(first.JournalEntry.Id, duplicate.JournalEntry.Id);
            Assert.Equal(1, await dbContext.JournalEntries.CountAsync());
            Assert.Equal(
                75m,
                (await dbContext.Wallets.SingleAsync(
                    wallet => wallet.UserId == "postgres-payer")).Balance);
        }

        [Fact]
        public async Task PostTransferAsync_ConcurrentDebits_CannotOverdraw()
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
                await SeedConcurrentAccountsAsync(setup);
            }

            var first = TryPostAsync(
                options,
                ConcurrentTransfer("concurrent-transfer-1", "concurrent-payee-1"));
            var second = TryPostAsync(
                options,
                ConcurrentTransfer("concurrent-transfer-2", "concurrent-payee-2"));
            var outcomes = await Task.WhenAll(first, second);

            Assert.Single(outcomes, outcome => outcome == null);
            Assert.Single(
                outcomes,
                outcome => outcome is InsufficientLedgerFundsException);
            await using var verification = new PaymentDbContext(options);
            Assert.Equal(
                20m,
                (await verification.Wallets.SingleAsync(
                    wallet => wallet.UserId == "concurrent-payer")).Balance);
            Assert.Equal(
                1,
                await verification.JournalEntries.CountAsync(entry =>
                    entry.IdempotencyKey.StartsWith("concurrent-transfer-")));
        }

        private static LedgerTransfer Transfer() => new()
        {
            IdempotencyKey = "postgres-ledger-service-test",
            Operation = JournalEntry.OperationLegacyPayment,
            Currency = "USD",
            Amount = 25m,
            DebitAccount = new LedgerAccountReference(
                "postgres-payer",
                LedgerAccount.TypeUserWallet),
            CreditAccount = new LedgerAccountReference(
                "postgres-payee",
                LedgerAccount.TypeUserWallet),
            Description = "PostgreSQL ledger service test"
        };

        private static LedgerTransfer ConcurrentTransfer(
            string idempotencyKey,
            string payeeUserId) => new()
        {
            IdempotencyKey = idempotencyKey,
            Operation = JournalEntry.OperationLegacyPayment,
            Currency = "USD",
            Amount = 80m,
            DebitAccount = new LedgerAccountReference(
                "concurrent-payer",
                LedgerAccount.TypeUserWallet),
            CreditAccount = new LedgerAccountReference(
                payeeUserId,
                LedgerAccount.TypeUserWallet),
            Description = "Concurrent debit test"
        };

        private static async Task<Exception?> TryPostAsync(
            DbContextOptions<PaymentDbContext> options,
            LedgerTransfer transfer)
        {
            await using var dbContext = new PaymentDbContext(options);
            var service = new LedgerService(
                dbContext,
                TimeProvider.System,
                NullLogger<LedgerService>.Instance);
            try
            {
                await service.PostTransferAsync(transfer);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static async Task SeedAsync(PaymentDbContext dbContext)
        {
            var payer = new LedgerAccount
            {
                OwnerUserId = "postgres-payer",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            var payee = new LedgerAccount
            {
                OwnerUserId = "postgres-payee",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            dbContext.LedgerAccounts.AddRange(payer, payee);
            dbContext.Wallets.AddRange(
                new UserWallet
                {
                    UserId = "postgres-payer",
                    Balance = 100m,
                    LedgerAccountId = payer.Id
                },
                new UserWallet
                {
                    UserId = "postgres-payee",
                    Balance = 0m,
                    LedgerAccountId = payee.Id
                });
            await dbContext.SaveChangesAsync();
        }

        private static async Task SeedConcurrentAccountsAsync(
            PaymentDbContext dbContext)
        {
            var payer = new LedgerAccount
            {
                OwnerUserId = "concurrent-payer",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            var firstPayee = new LedgerAccount
            {
                OwnerUserId = "concurrent-payee-1",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            var secondPayee = new LedgerAccount
            {
                OwnerUserId = "concurrent-payee-2",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            dbContext.LedgerAccounts.AddRange(payer, firstPayee, secondPayee);
            dbContext.Wallets.AddRange(
                new UserWallet
                {
                    UserId = "concurrent-payer",
                    Balance = 100m,
                    LedgerAccountId = payer.Id
                },
                new UserWallet
                {
                    UserId = "concurrent-payee-1",
                    Balance = 0m,
                    LedgerAccountId = firstPayee.Id
                },
                new UserWallet
                {
                    UserId = "concurrent-payee-2",
                    Balance = 0m,
                    LedgerAccountId = secondPayee.Id
                });
            await dbContext.SaveChangesAsync();
        }
    }
}
