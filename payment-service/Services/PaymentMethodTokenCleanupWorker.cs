using Microsoft.Extensions.Options;

namespace payment_service.Services
{
    public class PaymentMethodTokenCleanupWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<PaymentMethodTokenCleanupWorker> _logger;
        private readonly TimeSpan _cleanupInterval;

        public PaymentMethodTokenCleanupWorker(
            IServiceScopeFactory scopeFactory,
            TimeProvider timeProvider,
            IOptions<PaymentMethodTokenOptions> options,
            ILogger<PaymentMethodTokenCleanupWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _timeProvider = timeProvider;
            _logger = logger;

            if (options.Value.CleanupIntervalSeconds <= 0)
            {
                throw new InvalidOperationException(
                    "PaymentMethodTokens:CleanupIntervalSeconds must be greater than zero.");
            }

            _cleanupInterval = TimeSpan.FromSeconds(options.Value.CleanupIntervalSeconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var cleanupService =
                        scope.ServiceProvider.GetRequiredService<IPaymentMethodTokenCleanupService>();
                    var deleted = await cleanupService.DeleteRetainedTokensAsync(stoppingToken);
                    if (deleted > 0)
                    {
                        _logger.LogInformation(
                            "Deleted {TokenCount} expired or redeemed payment-method token records",
                            deleted);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to clean up payment-method token records");
                }

                await Task.Delay(_cleanupInterval, _timeProvider, stoppingToken);
            }
        }
    }
}
