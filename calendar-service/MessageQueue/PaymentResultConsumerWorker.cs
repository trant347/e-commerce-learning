using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
        private readonly string _topic;
        private readonly TimeSpan _failureRetryDelay;

        public PaymentResultConsumerWorker(
            ILogger<PaymentResultConsumerWorker> logger,
            IServiceProvider serviceProvider,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
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
                        _logger.LogError(
                            ex,
                            "Payment result failed; rewinding partition to {TopicPartitionOffset} for redelivery",
                            consumeResult?.TopicPartitionOffset);
                        if (consumeResult != null)
                        {
                            await RewindForRetryAsync(
                                consumer,
                                consumeResult,
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
            _logger.LogInformation(
                "Processed payment result sagaId={SagaId} transactionId={TransactionId} outcome={Outcome}; committed offset {TopicPartitionOffset}",
                result.SagaId,
                result.TransactionId,
                outcome,
                consumeResult.TopicPartitionOffset);
            return outcome;
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
    }
}
