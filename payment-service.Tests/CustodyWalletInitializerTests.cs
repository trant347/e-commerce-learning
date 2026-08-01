using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Data;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    /// <summary>
    /// Verifies that the configured custody account is created with no synthetic user balance
    /// and remains idempotent when startup initialization is repeated.
    /// </summary>
    public class CustodyWalletInitializerTests
    {
        [Fact]
        public async Task StartAsync_RepeatedInitialization_CreatesOneZeroBalanceWallet()
        {
            var services = new ServiceCollection();
            services.AddDbContext<PaymentDbContext>(options =>
                options.UseInMemoryDatabase(
                    nameof(StartAsync_RepeatedInitialization_CreatesOneZeroBalanceWallet)));
            using var provider = services.BuildServiceProvider();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Escrow:CustodyUserId"] = "configured-custody"
                })
                .Build();
            var first = NewInitializer(provider, configuration);
            var second = NewInitializer(provider, configuration);

            await first.StartAsync(CancellationToken.None);
            await second.StartAsync(CancellationToken.None);

            using var scope = provider.CreateScope();
            var dbContext =
                scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            var wallet = await dbContext.Wallets.SingleAsync();
            Assert.Equal("configured-custody", wallet.UserId);
            Assert.Equal(0m, wallet.Balance);
        }

        private static CustodyWalletInitializer NewInitializer(
            IServiceProvider provider,
            IConfiguration configuration) => new(
                provider,
                configuration,
                NullLogger<CustodyWalletInitializer>.Instance);
    }
}
