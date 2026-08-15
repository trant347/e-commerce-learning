using Microsoft.Extensions.Options;

namespace payment_service.Services
{
    public sealed class LedgerCutoverInitializer : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly LedgerCutoverOptions _options;
        private readonly ILogger<LedgerCutoverInitializer> _logger;

        public LedgerCutoverInitializer(
            IServiceProvider serviceProvider,
            IOptions<LedgerCutoverOptions> options,
            ILogger<LedgerCutoverInitializer> logger)
        {
            _serviceProvider = serviceProvider;
            _options = options.Value;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation(
                    "Ledger cutover is disabled; payment consumers will use the current rollout state");
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var cutover = scope.ServiceProvider
                .GetRequiredService<ILedgerCutoverService>();
            await cutover.ExecuteAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
