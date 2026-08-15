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
            var ledgerAccounts =
                scope.ServiceProvider.GetRequiredService<ILedgerAccountService>();
            var account = await ledgerAccounts.EnsureCustodyAccountAsync(
                custodyUserId,
                cancellationToken: cancellationToken);
            _logger.LogInformation(
                "Configured escrow custody account ensured userId={CustodyUserId} accountId={AccountId}",
                custodyUserId,
                account.Id);
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
