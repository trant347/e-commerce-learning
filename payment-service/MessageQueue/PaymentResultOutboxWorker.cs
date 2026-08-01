using System.Text.Json;
using Payment.Contracts;
using Payment.Contracts.V1;
using payment_service.Models;
using payment_service.Observability;

namespace payment_service.MessageQueue
{
    public sealed class PaymentResultOutboxWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IPaymentResultProducer _producer;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<PaymentResultOutboxWorker> _logger;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _claimLease;
        private readonly TimeSpan _baseRetryDelay;
        private readonly TimeSpan _maxRetryDelay;
        private readonly int _maxBatchSize;

        public PaymentResultOutboxWorker(
            IServiceProvider serviceProvider,
            IPaymentResultProducer producer,
            TimeProvider timeProvider,
            ILogger<PaymentResultOutboxWorker> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _producer = producer;
            _timeProvider = timeProvider;
            _logger = logger;
            _pollInterval = PositiveSeconds(
                configuration,
                "PaymentResultOutbox:PollIntervalSeconds",
                2);
            _claimLease = PositiveSeconds(
                configuration,
                "PaymentResultOutbox:ClaimLeaseSeconds",
                30);
            var messageTimeout = TimeSpan.FromMilliseconds(
                configuration.GetValue(
                    "PaymentResultProducer:MessageTimeoutMs",
                    30000));
            if (_claimLease <= messageTimeout.Add(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException(
                    "PaymentResultOutbox:ClaimLeaseSeconds must exceed the Kafka message timeout by more than five seconds.");
            }
            _baseRetryDelay = PositiveSeconds(
                configuration,
                "PaymentResultOutbox:BaseRetryDelaySeconds",
                1);
            _maxRetryDelay = PositiveSeconds(
                configuration,
                "PaymentResultOutbox:MaxRetryDelaySeconds",
                60);
            if (_maxRetryDelay < _baseRetryDelay)
            {
                throw new InvalidOperationException(
                    "PaymentResultOutbox:MaxRetryDelaySeconds must be greater than or equal to BaseRetryDelaySeconds.");
            }

            _maxBatchSize = configuration.GetValue(
                "PaymentResultOutbox:MaxBatchSize",
                100);
            if (_maxBatchSize <= 0)
            {
                throw new InvalidOperationException(
                    "PaymentResultOutbox:MaxBatchSize must be greater than zero.");
            }
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_pollInterval);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Payment result outbox pass failed; retrying on the next poll");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            try
            {
                var backlog = await WithStoreAsync(
                    store => store.GetPendingCountAsync(cancellationToken));
                PaymentSagaMetrics.OutboxBacklog.Record(backlog);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not collect payment result outbox backlog; dispatch will continue");
            }
            var reconciled = await WithStoreAsync(
                store => store.ReconcileMissingAsync(cancellationToken));
            if (reconciled > 0)
            {
                _logger.LogInformation(
                    "Reconciled {Count} payment transactions missing result outbox rows",
                    reconciled);
            }

            for (var processed = 0; processed < _maxBatchSize; processed++)
            {
                var row = await WithStoreAsync(
                    store => store.TryClaimNextAsync(
                        _claimLease,
                        cancellationToken));
                if (row == null)
                {
                    return;
                }
                if (!row.DispatchClaimedAt.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Claimed payment result {row.Id:D} has no claim timestamp.");
                }

                var claimTimestamp = row.DispatchClaimedAt.Value;
                var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    var result = JsonSerializer.Deserialize<PaymentResultV1>(
                        row.Payload,
                        PaymentContractJson.SerializerOptions)
                        ?? throw new JsonException(
                            $"Payment result outbox row {row.Id:D} has an empty payload.");
                    if (result.SagaId != row.SagaId
                        || result.TransactionId != row.TransactionId)
                    {
                        throw new InvalidOperationException(
                            $"Payment result outbox row {row.Id:D} identity does not match its payload.");
                    }

                    await _producer.PublishAsync(
                        result,
                        row.TraceParent,
                        cancellationToken);
                    var acknowledged = await WithStoreAsync(
                        store => store.MarkDispatchedAsync(
                            row.Id,
                            claimTimestamp,
                            cancellationToken));
                    if (!acknowledged)
                    {
                        _logger.LogWarning(
                            "Published payment result sagaId={SagaId}, but its dispatch lease was no longer current",
                            row.SagaId);
                    }
                    PaymentSagaMetrics.ProcessingDuration.Record(
                        System.Diagnostics.Stopwatch.GetElapsedTime(startedAt)
                            .TotalMilliseconds,
                        new KeyValuePair<string, object?>(
                            "stage",
                            "result_publication"));
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var nextAttemptAt = UtcNow().Add(
                        RetryDelay(row.DispatchAttemptCount));
                    var rescheduled = await WithStoreAsync(
                        store => store.RescheduleAsync(
                            row.Id,
                            claimTimestamp,
                            nextAttemptAt,
                            ex.Message,
                            cancellationToken));
                    if (!rescheduled)
                    {
                        _logger.LogWarning(
                            ex,
                            "Payment result sagaId={SagaId} failed publication after its dispatch lease was lost",
                            row.SagaId);
                        continue;
                    }

                    _logger.LogWarning(
                        ex,
                        "Payment result sagaId={SagaId} publication attempt {Attempt} failed; retrying at {NextAttemptAt}",
                        row.SagaId,
                        row.DispatchAttemptCount,
                        nextAttemptAt);
                    PaymentSagaMetrics.OutboxRetries.Add(
                        1,
                        new KeyValuePair<string, object?>(
                            "outbox",
                            "payment_results"));
                }
            }
        }

        private async Task<T> WithStoreAsync<T>(
            Func<IPaymentResultOutboxStore, Task<T>> action)
        {
            using var scope = _serviceProvider.CreateScope();
            var store = scope.ServiceProvider
                .GetRequiredService<IPaymentResultOutboxStore>();
            return await action(store);
        }

        private TimeSpan RetryDelay(int attemptCount)
        {
            var exponent = Math.Max(0, Math.Min(attemptCount - 1, 30));
            var seconds =
                _baseRetryDelay.TotalSeconds * Math.Pow(2, exponent);
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

        private DateTime UtcNow() =>
            _timeProvider.GetUtcNow().UtcDateTime;
    }
}
