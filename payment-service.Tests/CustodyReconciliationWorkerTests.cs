using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Data;
using payment_service.Models;
using payment_service.Observability;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class CustodyReconciliationWorkerTests
    {
        [Fact]
        public async Task RunOnceAsync_CustodyEqualsFundedEscrows_IsBalanced()
        {
            var provider = BuildProvider(nameof(
                RunOnceAsync_CustodyEqualsFundedEscrows_IsBalanced));
            await SeedAsync(provider, custodyBalance: 150m);
            var worker = BuildWorker(provider);

            var result = await worker.RunOnceAsync(CancellationToken.None);

            Assert.True(result.IsBalanced);
            Assert.Equal(150m, result.FundedEscrowValue);
            Assert.Equal(2, result.FundedEscrowCount);
        }

        [Fact]
        public async Task RunOnceAsync_CustodyDiffersFromFundedEscrows_ReportsDifference()
        {
            var provider = BuildProvider(nameof(
                RunOnceAsync_CustodyDiffersFromFundedEscrows_ReportsDifference));
            await SeedAsync(provider, custodyBalance: 140m);
            var worker = BuildWorker(provider);

            var result = await worker.RunOnceAsync(CancellationToken.None);

            Assert.False(result.IsBalanced);
            Assert.Equal(-10m, result.Difference);
        }

        [Fact]
        public async Task RunOnceAsync_ProjectionDiffersFromJournal_ReportsLedgerAnomaly()
        {
            var provider = BuildProvider(nameof(
                RunOnceAsync_ProjectionDiffersFromJournal_ReportsLedgerAnomaly));
            await SeedJournalBackedCustodyAsync(
                provider,
                cachedBalance: 140m,
                journalBalance: 150m);
            var worker = BuildWorker(provider);

            var result = await worker.RunOnceAsync(CancellationToken.None);

            Assert.True(result.IsBalanced);
            Assert.False(result.IsHealthy);
            Assert.Equal(1, result.ProjectionMismatchCount);
            Assert.Equal(10m, result.ProjectionMismatchValue);
        }

        [Fact]
        public async Task RunOnceAsync_PostCutoverApprovedPaymentWithoutJournal_IsReported()
        {
            var provider = BuildProvider(nameof(
                RunOnceAsync_PostCutoverApprovedPaymentWithoutJournal_IsReported));
            await SeedJournalBackedCustodyAsync(
                provider,
                cachedBalance: 150m,
                journalBalance: 150m);
            using (var scope = provider.CreateScope())
            {
                var dbContext =
                    scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
                var epoch = DateTime.UtcNow.AddMinutes(-5);
                dbContext.LedgerCutoverStates.Add(new LedgerCutoverState
                {
                    LedgerEpochAt = epoch,
                    CompletedAt = epoch,
                    WalletCount = 1
                });
                dbContext.Transactions.Add(new PaymentTransaction
                {
                    Amount = 25m,
                    Currency = "USD",
                    MaskedCardNumber = "ESCROW",
                    OwnerName = "requester",
                    Status = PaymentTransaction.StatusApproved,
                    Operation = JournalEntry.OperationFundEscrow,
                    PayerUserId = "requester",
                    PayeeUserId = "admin-custody",
                    CreatedAt = DateTime.UtcNow
                });
                await dbContext.SaveChangesAsync();
            }
            var worker = BuildWorker(provider);

            var result = await worker.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.MissingApprovedJournalCount);
            Assert.False(result.IsHealthy);
        }

        [Fact]
        public async Task RunOnceAsync_PostgresHealthyLedger_HasNoAnomalies()
        {
            var connectionString =
                Environment.GetEnvironmentVariable("PAYMENT_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            var services = new ServiceCollection();
            services.AddDbContext<PaymentDbContext>(options =>
                options.UseNpgsql(connectionString));
            using var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var dbContext =
                    scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
                await dbContext.Database.MigrateAsync();
            }
            await SeedJournalBackedCustodyAsync(
                provider,
                cachedBalance: 150m,
                journalBalance: 150m);
            var worker = BuildWorker(provider);

            var result = await worker.RunOnceAsync(CancellationToken.None);

            Assert.True(result.IsHealthy);
            Assert.Equal(0, result.AppendOnlyProtectionMissingCount);
        }

        private static ServiceProvider BuildProvider(string databaseName)
        {
            var services = new ServiceCollection();
            services.AddDbContext<PaymentDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            return services.BuildServiceProvider();
        }

        private static CustodyReconciliationWorker BuildWorker(
            ServiceProvider provider)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Escrow:CustodyUserId"] = "admin-custody",
                    ["EscrowReconciliation:PollIntervalSeconds"] = "60"
                })
                .Build();
            return new CustodyReconciliationWorker(
                provider,
                NullLogger<CustodyReconciliationWorker>.Instance,
                TimeProvider.System,
                new LedgerReconciliationHealthState(),
                configuration);
        }

        private static async Task SeedAsync(
            ServiceProvider provider,
            decimal custodyBalance)
        {
            using var scope = provider.CreateScope();
            var dbContext =
                scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            dbContext.Wallets.Add(new UserWallet
            {
                UserId = "admin-custody",
                Balance = custodyBalance
            });
            dbContext.Escrows.AddRange(
                NewEscrow(100m, EscrowRecord.StatusFunded),
                NewEscrow(50m, EscrowRecord.StatusFunded),
                NewEscrow(25m, EscrowRecord.StatusReleased));
            await dbContext.SaveChangesAsync();
        }

        private static async Task SeedJournalBackedCustodyAsync(
            ServiceProvider provider,
            decimal cachedBalance,
            decimal journalBalance)
        {
            using var scope = provider.CreateScope();
            var dbContext =
                scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            var custody = new LedgerAccount
            {
                OwnerUserId = "admin-custody",
                AccountType = LedgerAccount.TypeEscrowCustody,
                Currency = "USD"
            };
            var issuance = new LedgerAccount
            {
                AccountType = LedgerAccount.TypeSystemIssuance,
                Currency = "USD"
            };
            var entry = new JournalEntry
            {
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Operation = JournalEntry.OperationOpeningBalance,
                Currency = "USD",
                Description = "Reconciliation test",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10)
            };
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 1,
                AccountId = issuance.Id,
                Direction = JournalLine.DirectionDebit,
                Amount = journalBalance,
                CreatedAt = entry.CreatedAt
            });
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 2,
                AccountId = custody.Id,
                Direction = JournalLine.DirectionCredit,
                Amount = journalBalance,
                CreatedAt = entry.CreatedAt
            });
            dbContext.LedgerAccounts.AddRange(custody, issuance);
            dbContext.JournalEntries.Add(entry);
            dbContext.Wallets.Add(new UserWallet
            {
                UserId = "admin-custody",
                Balance = cachedBalance,
                LedgerAccountId = custody.Id
            });
            dbContext.Escrows.AddRange(
                NewEscrow(100m, EscrowRecord.StatusFunded),
                NewEscrow(50m, EscrowRecord.StatusFunded));
            await dbContext.SaveChangesAsync();
        }

        private static EscrowRecord NewEscrow(
            decimal amount,
            string status) => new()
        {
            Id = Guid.NewGuid(),
            BookingId = Guid.NewGuid().ToString("N"),
            Amount = amount,
            Currency = "USD",
            RequesterUserId = "requester",
            TaskMasterUserId = "taskmaster",
            CustodyUserId = "admin-custody",
            Status = status,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-30),
            FundedAt = DateTime.UtcNow.AddMinutes(-30)
        };
    }
}
