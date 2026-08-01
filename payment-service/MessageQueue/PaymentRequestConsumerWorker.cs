using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Confluent.Kafka;
using payment_service.Observability;
using payment_service.Services;
using Payment.Contracts;
using Payment.Contracts.V1;

namespace payment_service.MessageQueue
{
    public sealed class PaymentRequestConsumerWorker : BackgroundService
    {
        private static readonly ActivitySource s_activitySource =
            new("Kafka.Consumer");

        private readonly ILogger<PaymentRequestConsumerWorker> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConsumerConfig _consumerConfig;
        private readonly IKafkaDeadLetterProducer _deadLetterProducer;
        private readonly string _topic;
        private readonly TimeSpan _failureRetryDelay;
        private readonly int _maxInvalidMessageAttempts;
        private readonly ConcurrentDictionary<TopicPartitionOffset, int>
            _failureAttempts = new();

        public PaymentRequestConsumerWorker(
            ILogger<PaymentRequestConsumerWorker> logger,
            IServiceProvider serviceProvider,
            IKafkaDeadLetterProducer deadLetterProducer,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _deadLetterProducer = deadLetterProducer;
            _topic = configuration["KafkaConsumerConfig:PaymentRequestTopic"]
                ?? "payment-requests";
            var retryDelaySeconds = configuration.GetValue(
                "KafkaConsumerConfig:PaymentRequestFailureRetryDelaySeconds",
                5);
            if (retryDelaySeconds <= 0)
            {
                throw new InvalidOperationException(
                    "KafkaConsumerConfig:PaymentRequestFailureRetryDelaySeconds must be greater than zero");
            }

            _failureRetryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
            _maxInvalidMessageAttempts = configuration.GetValue(
                "KafkaConsumerConfig:PaymentRequestMaxInvalidMessageAttempts",
                3);
            if (_maxInvalidMessageAttempts <= 0)
            {
                throw new InvalidOperationException(
                    "KafkaConsumerConfig:PaymentRequestMaxInvalidMessageAttempts must be greater than zero");
            }
            _consumerConfig = new ConsumerConfig
            {
                BootstrapServers =
                    configuration["KafkaConsumerConfig:BootstrapServers"]
                    ?? throw new InvalidOperationException(
                        "KafkaConsumerConfig:BootstrapServers is required"),
                GroupId =
                    configuration["KafkaConsumerConfig:PaymentRequestGroupId"]
                    ?? "payment-service-payment-requests-v1",
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
                "PaymentRequestConsumerWorker started, listening to topic {Topic} with group {GroupId}",
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
                        _logger.LogError(
                            ex,
                            "Error consuming payment request");
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
                    "PaymentRequestConsumerWorker is stopping");
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

        public async Task<PaymentResultV1> ProcessConsumeResultAsync(
            IConsumer<string, string> consumer,
            ConsumeResult<string, string> consumeResult,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(consumer);
            ArgumentNullException.ThrowIfNull(consumeResult);
            if (string.IsNullOrWhiteSpace(consumeResult.Message?.Value))
            {
                throw new JsonException(
                    "Payment request message body is required.");
            }

            var request = JsonSerializer.Deserialize<PaymentRequestedV1>(
                consumeResult.Message.Value,
                PaymentContractJson.SerializerOptions)
                ?? throw new JsonException(
                    "Payment request message could not be deserialized.");
            if (!string.Equals(
                consumeResult.Message.Key,
                request.KafkaMessageKey,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Payment request Kafka key does not match sagaId.");
            }

            using var activity = StartConsumerActivity(consumeResult);
            var startedAt = Stopwatch.GetTimestamp();
            activity?.SetTag("payment.saga_id", request.SagaId);
            activity?.SetTag("payment.escrow_id", request.EscrowId);
            activity?.SetTag("payment.operation", request.Operation);

            using var scope = _serviceProvider.CreateScope();
            var processor =
                scope.ServiceProvider.GetRequiredService<IPaymentRequestProcessor>();
            var result = await processor.ProcessAsync(
                request,
                cancellationToken);

            consumer.Commit(consumeResult);
            _failureAttempts.TryRemove(
                consumeResult.TopicPartitionOffset,
                out _);
            PaymentSagaMetrics.ProcessingDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", request.Operation),
                new KeyValuePair<string, object?>("status", result.Status));
            RecordConsumerLag(consumer, consumeResult);
            _logger.LogInformation(
                "Processed payment request sagaId={SagaId} transactionId={TransactionId} status={Status}; committed offset {TopicPartitionOffset}",
                result.SagaId,
                result.TransactionId,
                result.Status,
                consumeResult.TopicPartitionOffset);
            return result;
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
                        "Payment request DLQ publication failed at {TopicPartitionOffset}; retaining the source offset for retry",
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
                "Payment request processing attempt {Attempt} failed at {TopicPartitionOffset}; rewinding for redelivery",
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
                or KeyNotFoundException
            && exception is not PaymentRequestRetryableException;

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
