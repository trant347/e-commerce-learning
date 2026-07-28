using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Payment.Contracts;
using Payment.Contracts.V1;

namespace payment_service.MessageQueue
{
    public sealed class PaymentResultProducer : IPaymentResultProducer, IDisposable
    {
        private static readonly ActivitySource s_activitySource =
            new("Kafka.Producer");

        private readonly IProducer<string, string> _producer;
        private readonly string _topic;
        private readonly ILogger<PaymentResultProducer> _logger;
        private readonly bool _ownsProducer;

        public PaymentResultProducer(
            IOptions<PaymentResultProducerOptions> options,
            ILogger<PaymentResultProducer> logger)
        {
            var config = options.Value;
            if (string.IsNullOrWhiteSpace(config.BootstrapServers))
            {
                throw new InvalidOperationException(
                    "PaymentResultProducer:BootstrapServers is required.");
            }
            if (config.MessageTimeoutMs <= 0)
            {
                throw new InvalidOperationException(
                    "PaymentResultProducer:MessageTimeoutMs must be greater than zero.");
            }

            _topic = string.IsNullOrWhiteSpace(config.Topic)
                ? "payment-results"
                : config.Topic.Trim();
            _logger = logger;
            _producer = new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = config.BootstrapServers,
                ClientId = config.ClientId + "-result-outbox",
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageTimeoutMs = config.MessageTimeoutMs
            }).Build();
            _ownsProducer = true;
        }

        public PaymentResultProducer(
            IProducer<string, string> producer,
            string topic,
            ILogger<PaymentResultProducer> logger)
        {
            _producer = producer;
            _topic = string.IsNullOrWhiteSpace(topic)
                ? throw new ArgumentException(
                    "Payment result topic is required.",
                    nameof(topic))
                : topic.Trim();
            _logger = logger;
        }

        public async Task PublishAsync(
            PaymentResultV1 result,
            string? traceParent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(result);
            var message = new Message<string, string>
            {
                Key = result.KafkaMessageKey,
                Value = JsonSerializer.Serialize(
                    result,
                    PaymentContractJson.SerializerOptions)
            };
            if (!string.IsNullOrWhiteSpace(traceParent))
            {
                message.Headers = new Headers
                {
                    {
                        "traceparent",
                        Encoding.UTF8.GetBytes(traceParent.Trim())
                    }
                };
            }

            using var activity = s_activitySource.StartActivity(
                $"{_topic} publish",
                ActivityKind.Producer);
            activity?.SetTag("messaging.system", "kafka");
            activity?.SetTag("messaging.destination.name", _topic);
            activity?.SetTag("messaging.kafka.message.key", message.Key);

            var delivery = await _producer.ProduceAsync(
                _topic,
                message,
                cancellationToken);
            if (delivery.Status != PersistenceStatus.Persisted)
            {
                throw new InvalidOperationException(
                    $"Kafka did not persist payment result {result.SagaId:D}; status={delivery.Status}.");
            }

            _logger.LogInformation(
                "Published payment result sagaId={SagaId} transactionId={TransactionId} status={Status}",
                result.SagaId,
                result.TransactionId,
                result.Status);
        }

        public void Dispose()
        {
            if (!_ownsProducer)
            {
                return;
            }

            _producer.Flush(TimeSpan.FromSeconds(5));
            _producer.Dispose();
        }
    }
}
