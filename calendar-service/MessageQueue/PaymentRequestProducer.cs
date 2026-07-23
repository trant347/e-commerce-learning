using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Payment.Contracts;
using Payment.Contracts.V1;

namespace calendar_service.MessageQueue
{
    public sealed class PaymentRequestProducer : IPaymentRequestProducer, IDisposable
    {
        private static readonly ActivitySource s_activitySource = new("Kafka.Producer");

        private readonly IProducer<string, string> _producer;
        private readonly string _topic;
        private readonly ILogger<PaymentRequestProducer> _logger;
        private readonly bool _ownsProducer;

        public PaymentRequestProducer(
            IOptions<KafkaProducerConfig> options,
            ILogger<PaymentRequestProducer> logger)
        {
            var config = options.Value;
            _topic = string.IsNullOrWhiteSpace(config.PaymentRequestTopic)
                ? "payment-requests"
                : config.PaymentRequestTopic.Trim();
            if (config.PaymentRequestMessageTimeoutMs <= 0)
            {
                throw new InvalidOperationException(
                    "KafkaProducerConfig:PaymentRequestMessageTimeoutMs must be greater than zero.");
            }

            _logger = logger;
            _producer = new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = config.BootstrapServers,
                ClientId = (config.ClientId ?? "calendar-service") + "-payment-outbox",
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageTimeoutMs = config.PaymentRequestMessageTimeoutMs
            }).Build();
            _ownsProducer = true;
        }

        public PaymentRequestProducer(
            IProducer<string, string> producer,
            string topic,
            ILogger<PaymentRequestProducer> logger)
        {
            _producer = producer;
            _topic = string.IsNullOrWhiteSpace(topic)
                ? throw new ArgumentException("Payment request topic is required.", nameof(topic))
                : topic.Trim();
            _logger = logger;
        }

        public async Task PublishAsync(
            PaymentRequestedV1 request,
            string? traceParent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            request.Validate();

            var message = new Message<string, string>
            {
                Key = request.KafkaMessageKey,
                Value = JsonSerializer.Serialize(
                    request,
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
                    $"Kafka did not persist payment request {request.SagaId:D}; status={delivery.Status}.");
            }

            _logger.LogInformation(
                "Published payment request sagaId={SagaId} escrowId={EscrowId} operation={Operation} topic={Topic}",
                request.SagaId,
                request.EscrowId,
                request.Operation,
                _topic);
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
