using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class LedgerCutoverServiceTests
    {
        private static readonly DateTimeOffset Epoch =
            new(2034, 2, 3, 4, 5, 6, TimeSpan.Zero);

        [Fact]
        public async Task ExecuteAsync_CreatesOpeningEntriesAndReconcilesWallets()
        {
            await using var dbContext = NewContext();
            await SeedLegacyStateAsync(dbContext);
            var service = NewService(dbContext);

            var state = await service.ExecuteAsync();

            Assert.Equal(Epoch.UtcDateTime, state.LedgerEpochAt);
            Assert.Equal(3, state.WalletCount);
            Assert.Equal(4, await dbContext.LedgerAccounts.CountAsync());
            Assert.Equal(2, await dbContext.JournalEntries.CountAsync());
            Assert.All(
                await dbContext.JournalEntries.ToListAsync(),
                entry => Assert.Equal(
                    JournalEntry.OperationOpeningBalance,
                    entry.Operation));
            var queries = new LedgerQueryService(dbContext);
            foreach (var wallet in await dbContext.Wallets.ToListAsync())
            {
                Assert.NotNull(wallet.LedgerAccountId);
                Assert.Equal(
                    wallet.Balance,
                    await queries.GetJournalBalanceAsync(
                        wallet.LedgerAccountId!.Value));
            }
        }

        [Fact]
        public async Task ExecuteAsync_RepeatedRun_DoesNotDuplicateOpeningEntries()
        {
            await using var dbContext = NewContext();
            await SeedLegacyStateAsync(dbContext);
            var service = NewService(dbContext);

            var first = await service.ExecuteAsync();
            var second = await service.ExecuteAsync();

            Assert.Equal(first.LedgerEpochAt, second.LedgerEpochAt);
            Assert.Equal(1, await dbContext.LedgerCutoverStates.CountAsync());
            Assert.Equal(2, await dbContext.JournalEntries.CountAsync());
        }

        [Fact]
        public async Task ExecuteAsync_ExistingCutoverWithMismatch_BlocksStartup()
        {
            await using var dbContext = NewContext();
            await SeedLegacyStateAsync(dbContext);
            var service = NewService(dbContext);
            await service.ExecuteAsync();
            var requester = await dbContext.Wallets.SingleAsync(
                wallet => wallet.UserId == "requester");
            requester.Balance += 1m;
            await dbContext.SaveChangesAsync();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ExecuteAsync());

            Assert.Contains("does not match journal balance", exception.Message);
        }

        [Fact]
        public async Task ExecuteAsync_RunsOnPostgresAndIsRestartSafe()
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
            await SeedLegacyStateAsync(dbContext);
            var service = NewService(dbContext);

            var first = await service.ExecuteAsync();
            dbContext.ChangeTracker.Clear();
            var second = await service.ExecuteAsync();

            Assert.Equal(first.LedgerEpochAt, second.LedgerEpochAt);
            Assert.Equal(2, await dbContext.JournalEntries.CountAsync());
            Assert.Equal(1, await dbContext.LedgerCutoverStates.CountAsync());
        }

        [Fact]
        public async Task ExecuteAsync_ConcurrentPostgresCutovers_CommitOnce()
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
                await SeedLegacyStateAsync(setup);
            }

            var first = ExecuteWithNewContextAsync(options);
            var second = ExecuteWithNewContextAsync(options);
            await Task.WhenAll(first, second);

            await using var verification = new PaymentDbContext(options);
            Assert.Equal(1, await verification.LedgerCutoverStates.CountAsync());
            Assert.Equal(2, await verification.JournalEntries.CountAsync());
        }

        private static PaymentDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new PaymentDbContext(options);
        }

        private static LedgerCutoverService NewService(
            PaymentDbContext dbContext)
        {
            var ledgerAccounts = new LedgerAccountService(
                dbContext,
                new FixedTimeProvider(Epoch),
                NullLogger<LedgerAccountService>.Instance);
            var ledgerQueries = new LedgerQueryService(dbContext);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Escrow:CustodyUserId"] = "admin-custody"
                })
                .Build();
            return new LedgerCutoverService(
                dbContext,
                ledgerAccounts,
                ledgerQueries,
                Options.Create(new LedgerCutoverOptions
                {
                    Enabled = true,
                    Currency = "USD"
                }),
                configuration,
                new FixedTimeProvider(Epoch),
                NullLogger<LedgerCutoverService>.Instance);
        }

        private static async Task ExecuteWithNewContextAsync(
            DbContextOptions<PaymentDbContext> options)
        {
            await using var dbContext = new PaymentDbContext(options);
            await NewService(dbContext).ExecuteAsync();
        }

        private static async Task SeedLegacyStateAsync(
            PaymentDbContext dbContext)
        {
            dbContext.Wallets.AddRange(
                new UserWallet
                {
                    UserId = "requester",
                    Balance = 500m,
                    CreatedAt = Epoch.UtcDateTime.AddDays(-1),
                    UpdatedAt = Epoch.UtcDateTime.AddDays(-1)
                },
                new UserWallet
                {
                    UserId = "admin-custody",
                    Balance = 100m,
                    CreatedAt = Epoch.UtcDateTime.AddDays(-1),
                    UpdatedAt = Epoch.UtcDateTime.AddDays(-1)
                },
                new UserWallet
                {
                    UserId = "taskmaster",
                    Balance = 0m,
                    CreatedAt = Epoch.UtcDateTime.AddDays(-1),
                    UpdatedAt = Epoch.UtcDateTime.AddDays(-1)
                });
            dbContext.Escrows.Add(new EscrowRecord
            {
                Id = Guid.NewGuid(),
                BookingId = "cutover-booking",
                Amount = 100m,
                Currency = "USD",
                RequesterUserId = "requester",
                TaskMasterUserId = "taskmaster",
                CustodyUserId = "admin-custody",
                Status = EscrowRecord.StatusFunded,
                CreatedAt = Epoch.UtcDateTime.AddHours(-1),
                UpdatedAt = Epoch.UtcDateTime.AddHours(-1),
                FundedAt = Epoch.UtcDateTime.AddHours(-1),
                FundingTransactionId = Guid.NewGuid()
            });
            await dbContext.SaveChangesAsync();
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _utcNow;

            public FixedTimeProvider(DateTimeOffset utcNow)
            {
                _utcNow = utcNow;
            }

            public override DateTimeOffset GetUtcNow() => _utcNow;
        }
    }
}
