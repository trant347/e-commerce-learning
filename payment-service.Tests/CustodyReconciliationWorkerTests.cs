using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Data;
using payment_service.Models;
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
