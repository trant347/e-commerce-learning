using Microsoft.EntityFrameworkCore;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    /// <summary>
    /// Ensures the explicitly configured escrow custody account has a zero-balance wallet before
    /// payment consumers start; concurrent service replicas may safely run it more than once.
    /// </summary>
    public sealed class CustodyWalletInitializer : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CustodyWalletInitializer> _logger;

        public CustodyWalletInitializer(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<CustodyWalletInitializer> logger)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var custodyUserId = _configuration["Escrow:CustodyUserId"]?.Trim();
            if (string.IsNullOrWhiteSpace(custodyUserId))
            {
                throw new InvalidOperationException(
                    "Escrow:CustodyUserId is required.");
            }

            using var scope = _serviceProvider.CreateScope();
            var dbContext =
                scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            var existing = await dbContext.Wallets
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    wallet => wallet.UserId == custodyUserId,
                    cancellationToken);
            if (existing != null)
            {
                _logger.LogInformation(
                    "Using configured escrow custody wallet userId={CustodyUserId} balance={Balance}",
                    custodyUserId,
                    existing.Balance);
                return;
            }

            dbContext.Wallets.Add(new UserWallet
            {
                UserId = custodyUserId,
                Balance = 0m
            });
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Created configured escrow custody wallet userId={CustodyUserId} with zero balance",
                    custodyUserId);
            }
            catch (DbUpdateException)
            {
                dbContext.ChangeTracker.Clear();
                if (!await dbContext.Wallets
                    .AsNoTracking()
                    .AnyAsync(
                        wallet => wallet.UserId == custodyUserId,
                        cancellationToken))
                {
                    throw;
                }

                _logger.LogInformation(
                    "Another payment-service replica created escrow custody wallet userId={CustodyUserId}",
                    custodyUserId);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
