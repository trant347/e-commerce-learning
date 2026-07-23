using calendar_service.Services.Contracts;

namespace calendar_service.MessageQueue
{
    public sealed class PaymentRequestOutboxWorker : BackgroundService
    {
        private readonly ISagaStateService _sagaStateService;
        private readonly IPaymentRequestProducer _producer;
        private readonly ILogger<PaymentRequestOutboxWorker> _logger;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _claimLease;
        private readonly TimeSpan _baseRetryDelay;
        private readonly TimeSpan _maxRetryDelay;
        private readonly int _maxBatchSize;

        public PaymentRequestOutboxWorker(
            ISagaStateService sagaStateService,
            IPaymentRequestProducer producer,
            ILogger<PaymentRequestOutboxWorker> logger,
            IConfiguration configuration)
        {
            _sagaStateService = sagaStateService;
            _producer = producer;
            _logger = logger;
            _pollInterval = PositiveSeconds(
                configuration,
                "PaymentRequestOutbox:PollIntervalSeconds",
                2);
            _claimLease = PositiveSeconds(
                configuration,
                "PaymentRequestOutbox:ClaimLeaseSeconds",
                30);
            _baseRetryDelay = PositiveSeconds(
                configuration,
                "PaymentRequestOutbox:BaseRetryDelaySeconds",
                1);
            _maxRetryDelay = PositiveSeconds(
                configuration,
                "PaymentRequestOutbox:MaxRetryDelaySeconds",
                60);
            if (_maxRetryDelay < _baseRetryDelay)
            {
                throw new InvalidOperationException(
                    "PaymentRequestOutbox:MaxRetryDelaySeconds must be greater than or equal to BaseRetryDelaySeconds.");
            }

            _maxBatchSize = configuration.GetValue(
                "PaymentRequestOutbox:MaxBatchSize",
                100);
            if (_maxBatchSize <= 0)
            {
                throw new InvalidOperationException(
                    "PaymentRequestOutbox:MaxBatchSize must be greater than zero.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_pollInterval);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Payment request outbox pass failed; retrying on the next poll");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            for (var processed = 0; processed < _maxBatchSize; processed++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var saga = await _sagaStateService.TryClaimNextDispatchAsync(
                    _claimLease,
                    cancellationToken);
                if (saga == null)
                {
                    return;
                }
                if (saga.PaymentRequest == null
                    || !saga.DispatchClaimedAt.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Claimed saga {saga.SagaId:D} has no payment request or claim timestamp.");
                }

                var claimTimestamp = saga.DispatchClaimedAt.Value;
                try
                {
                    await _producer.PublishAsync(
                        saga.PaymentRequest.ToContract(),
                        saga.TraceParent,
                        cancellationToken);

                    var acknowledged = await _sagaStateService.MarkDispatchedAsync(
                        saga.SagaId,
                        claimTimestamp,
                        cancellationToken);
                    if (!acknowledged)
                    {
                        _logger.LogWarning(
                            "Published payment request sagaId={SagaId}, but its dispatch lease was no longer current",
                            saga.SagaId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var retryDelay = RetryDelay(saga.DispatchAttemptCount);
                    var nextAttemptAt = DateTime.UtcNow.Add(retryDelay);
                    var rescheduled = await _sagaStateService.RescheduleDispatchAsync(
                        saga.SagaId,
                        claimTimestamp,
                        nextAttemptAt,
                        ex.Message,
                        cancellationToken);
                    if (!rescheduled)
                    {
                        _logger.LogWarning(
                            ex,
                            "Payment request sagaId={SagaId} failed publication after its dispatch lease was lost",
                            saga.SagaId);
                        continue;
                    }

                    _logger.LogWarning(
                        ex,
                        "Payment request sagaId={SagaId} publication attempt {Attempt} failed; retrying at {NextAttemptAt}",
                        saga.SagaId,
                        saga.DispatchAttemptCount,
                        nextAttemptAt);
                }
            }
        }

        private TimeSpan RetryDelay(int attemptCount)
        {
            var exponent = Math.Max(0, Math.Min(attemptCount - 1, 30));
            var seconds = _baseRetryDelay.TotalSeconds * Math.Pow(2, exponent);
            return TimeSpan.FromSeconds(
                Math.Min(seconds, _maxRetryDelay.TotalSeconds));
        }

        private static TimeSpan PositiveSeconds(
            IConfiguration configuration,
            string key,
            int defaultValue)
        {
            var seconds = configuration.GetValue(key, defaultValue);
            if (seconds <= 0)
            {
                throw new InvalidOperationException(
                    $"{key} must be greater than zero.");
            }

            return TimeSpan.FromSeconds(seconds);
        }
    }
}
