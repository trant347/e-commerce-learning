using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using calendar_service.Observability;
using calendar_service.Services;
using Confluent.Kafka;
using Payment.Contracts;
using Payment.Contracts.V1;

namespace calendar_service.MessageQueue
{
    public sealed class PaymentResultConsumerWorker : BackgroundService
    {
        private static readonly ActivitySource s_activitySource =
            new("Kafka.Consumer");

        private readonly ILogger<PaymentResultConsumerWorker> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConsumerConfig _consumerConfig;
        private readonly IKafkaDeadLetterProducer _deadLetterProducer;
        private readonly string _topic;
        private readonly TimeSpan _failureRetryDelay;
        private readonly int _maxInvalidMessageAttempts;
        private readonly ConcurrentDictionary<TopicPartitionOffset, int>
            _failureAttempts = new();

        public PaymentResultConsumerWorker(
            ILogger<PaymentResultConsumerWorker> logger,
            IServiceProvider serviceProvider,
            IKafkaDeadLetterProducer deadLetterProducer,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _deadLetterProducer = deadLetterProducer;
            _topic = configuration["KafkaConsumerConfig:PaymentResultTopic"]
                ?? "payment-results";

            var retryDelaySeconds = configuration.GetValue(
                "KafkaConsumerConfig:PaymentResultFailureRetryDelaySeconds",
                5);
            if (retryDelaySeconds <= 0)
            {
                throw new InvalidOperationException(
                    "KafkaConsumerConfig:PaymentResultFailureRetryDelaySeconds must be greater than zero.");
            }

            _failureRetryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
            _maxInvalidMessageAttempts = configuration.GetValue(
                "KafkaConsumerConfig:PaymentResultMaxInvalidMessageAttempts",
                3);
            if (_maxInvalidMessageAttempts <= 0)
            {
                throw new InvalidOperationException(
                    "KafkaConsumerConfig:PaymentResultMaxInvalidMessageAttempts must be greater than zero.");
            }
            _consumerConfig = new ConsumerConfig
            {
                BootstrapServers =
                    configuration["KafkaConsumerConfig:BootstrapServers"]
                    ?? configuration["KafkaProducerConfig:BootstrapServers"]
                    ?? throw new InvalidOperationException(
                        "KafkaConsumerConfig:BootstrapServers is required."),
                GroupId =
                    configuration["KafkaConsumerConfig:PaymentResultGroupId"]
                    ?? "calendar-service-payment-results-v1",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false
            };
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            await Task.Yield();
            using var consumer = new ConsumerBuilder<string, string>(
                _consumerConfig).Build();
            consumer.Subscribe(_topic);
            _logger.LogInformation(
                "PaymentResultConsumerWorker started, listening to topic {Topic} with group {GroupId}",
                _topic,
                _consumerConfig.GroupId);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    ConsumeResult<string, string>? consumeResult = null;
                    try
                    {
                        consumeResult = consumer.Consume(stoppingToken);
                        if (consumeResult?.Message?.Value == null)
                        {
                            continue;
                        }

                        await ProcessConsumeResultAsync(
                            consumer,
                            consumeResult,
                            stoppingToken);
                    }
                    catch (ConsumeException ex)
                    {
                        _logger.LogError(ex, "Error consuming payment result.");
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (consumeResult != null)
                        {
                            await HandleFailureAsync(
                                consumer,
                                consumeResult,
                                ex,
                                stoppingToken);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "PaymentResultConsumerWorker is stopping.");
            }
            finally
            {
                consumer.Close();
            }
        }

        public async Task RewindForRetryAsync(
            IConsumer<string, string> consumer,
            ConsumeResult<string, string> consumeResult,
            CancellationToken cancellationToken)
        {
            consumer.Seek(consumeResult.TopicPartitionOffset);
            await Task.Delay(_failureRetryDelay, cancellationToken);
        }

        public async Task<PaymentResultProcessingOutcome> ProcessConsumeResultAsync(
            IConsumer<string, string> consumer,
            ConsumeResult<string, string> consumeResult,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            ArgumentNullException.ThrowIfNull(consumeResult);
            if (string.IsNullOrWhiteSpace(consumeResult.Message?.Value))
            {
                throw new JsonException(
                    "Payment result message body is required.");
            }

            var result = JsonSerializer.Deserialize<PaymentResultV1>(
                consumeResult.Message.Value,
                PaymentContractJson.SerializerOptions)
                ?? throw new JsonException(
                    "Payment result message could not be deserialized.");
            if (!string.Equals(
                consumeResult.Message.Key,
                result.KafkaMessageKey,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Payment result Kafka key does not match sagaId.");
            }

            using var activity = StartConsumerActivity(consumeResult);
            var startedAt = Stopwatch.GetTimestamp();
            activity?.SetTag("payment.saga_id", result.SagaId);
            activity?.SetTag("payment.escrow_id", result.EscrowId);
            activity?.SetTag("payment.operation", result.Operation);

            using var scope = _serviceProvider.CreateScope();
            var processor =
                scope.ServiceProvider.GetRequiredService<IPaymentResultProcessor>();
            var outcome = await processor.ProcessAsync(
                result,
                cancellationToken);

            consumer.Commit(consumeResult);
            _failureAttempts.TryRemove(
                consumeResult.TopicPartitionOffset,
                out _);
            PaymentSagaMetrics.ProcessingDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", result.Operation),
                new KeyValuePair<string, object?>("outcome", outcome.ToString()));
            RecordConsumerLag(consumer, consumeResult);
            _logger.LogInformation(
                "Processed payment result sagaId={SagaId} transactionId={TransactionId} outcome={Outcome}; committed offset {TopicPartitionOffset}",
                result.SagaId,
                result.TransactionId,
                outcome,
                consumeResult.TopicPartitionOffset);
            return outcome;
        }

        public async Task HandleFailureAsync(
            IConsumer<string, string> consumer,
            ConsumeResult<string, string> consumeResult,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var attempts = _failureAttempts.AddOrUpdate(
                consumeResult.TopicPartitionOffset,
                1,
                (_, current) => current + 1);
            if (IsPermanentlyInvalid(exception)
                && attempts >= _maxInvalidMessageAttempts)
            {
                try
                {
                    await _deadLetterProducer.PublishAsync(
                        consumeResult,
                        exception,
                        attempts,
                        cancellationToken);
                }
                catch (Exception dlqException)
                {
                    _logger.LogError(
                        dlqException,
                        "Payment result DLQ publication failed at {TopicPartitionOffset}; retaining the source offset for retry",
                        consumeResult.TopicPartitionOffset);
                    await RewindForRetryAsync(
                        consumer,
                        consumeResult,
                        cancellationToken);
                    return;
                }
                consumer.Commit(consumeResult);
                _failureAttempts.TryRemove(
                    consumeResult.TopicPartitionOffset,
                    out _);
                PaymentSagaMetrics.DeadLetters.Add(
                    1,
                    new KeyValuePair<string, object?>(
                        "source_topic",
                        consumeResult.Topic));
                return;
            }

            _logger.LogWarning(
                exception,
                "Payment result processing attempt {Attempt} failed at {TopicPartitionOffset}; rewinding for redelivery",
                attempts,
                consumeResult.TopicPartitionOffset);
            await RewindForRetryAsync(
                consumer,
                consumeResult,
                cancellationToken);
        }

        private Activity? StartConsumerActivity(
            ConsumeResult<string, string> result)
        {
            ActivityContext parentContext = default;
            var traceParent = result.Message?.Headers?
                .FirstOrDefault(header => header.Key == "traceparent");
            if (traceParent != null)
            {
                ActivityContext.TryParse(
                    Encoding.UTF8.GetString(traceParent.GetValueBytes()),
                    null,
                    out parentContext);
            }

            var activity = s_activitySource.StartActivity(
                $"{_topic} process",
                ActivityKind.Consumer,
                parentContext);
            activity?.SetTag("messaging.system", "kafka");
            activity?.SetTag("messaging.destination.name", _topic);
            activity?.SetTag("messaging.operation", "process");
            return activity;
        }

        private static bool IsPermanentlyInvalid(Exception exception) =>
            exception is JsonException
                or ArgumentException
                or InvalidOperationException
            && exception is not PaymentResultRetryableException;

        private static void RecordConsumerLag(
            IConsumer<string, string> consumer,
            ConsumeResult<string, string> result)
        {
            try
            {
                var watermark = consumer.GetWatermarkOffsets(
                    result.TopicPartition);
                if (watermark == null)
                {
                    return;
                }
                var lag = Math.Max(
                    0,
                    watermark.High.Value - result.Offset.Value - 1);
                PaymentSagaMetrics.ConsumerLag.Record(
                    lag,
                    new KeyValuePair<string, object?>("topic", result.Topic),
                    new KeyValuePair<string, object?>(
                        "partition",
                        result.Partition.Value));
            }
            catch (KafkaException)
            {
                // Lag is best-effort telemetry and must not affect offset commits.
            }
        }
    }
}
