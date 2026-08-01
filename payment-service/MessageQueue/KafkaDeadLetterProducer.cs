using System.Text;
using Confluent.Kafka;

namespace payment_service.MessageQueue
{
    /// <summary>
    /// Copies failed Kafka messages to a dead-letter topic while preserving their key and headers.
    /// </summary>
    public sealed class KafkaDeadLetterProducer : IKafkaDeadLetterProducer, IDisposable
    {
        private readonly IProducer<string, string> _producer;
        private readonly ILogger<KafkaDeadLetterProducer> _logger;

        public KafkaDeadLetterProducer(
            IConfiguration configuration,
            ILogger<KafkaDeadLetterProducer> logger)
        {
            var bootstrapServers =
                configuration["KafkaConsumerConfig:BootstrapServers"]
                ?? throw new InvalidOperationException(
                    "KafkaConsumerConfig:BootstrapServers is required.");
            _producer = new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                ClientId = "payment-service-payment-dlq",
                Acks = Acks.All,
                EnableIdempotence = true
            }).Build();
            _logger = logger;
        }

        public async Task PublishAsync(
            ConsumeResult<string, string> consumeResult,
            Exception exception,
            int attemptCount,
            CancellationToken cancellationToken)
        {
            var topic = consumeResult.Topic + ".dlq";
            var headers = new Headers();
            if (consumeResult.Message.Headers != null)
            {
                foreach (var header in consumeResult.Message.Headers)
                {
                    headers.Add(header.Key, header.GetValueBytes());
                }
            }
            headers.Remove("x-error-type");
            headers.Remove("x-error-message");
            headers.Remove("x-delivery-attempt");
            headers.Add(
                "x-error-type",
                Encoding.UTF8.GetBytes(exception.GetType().FullName ?? exception.GetType().Name));
            headers.Add(
                "x-error-message",
                Encoding.UTF8.GetBytes(Truncate(exception.Message, 1000)));
            headers.Add(
                "x-delivery-attempt",
                Encoding.UTF8.GetBytes(attemptCount.ToString()));

            var delivery = await _producer.ProduceAsync(
                topic,
                new Message<string, string>
                {
                    Key = consumeResult.Message.Key,
                    Value = consumeResult.Message.Value,
                    Headers = headers
                },
                cancellationToken);
            if (delivery.Status != PersistenceStatus.Persisted)
            {
                throw new InvalidOperationException(
                    $"Kafka did not persist dead-letter message for {consumeResult.TopicPartitionOffset}.");
            }

            _logger.LogError(
                exception,
                "Dead-lettered payment message sourceTopic={SourceTopic} dlqTopic={DlqTopic} partition={Partition} offset={Offset} attempts={Attempts}",
                consumeResult.Topic,
                topic,
                consumeResult.Partition.Value,
                consumeResult.Offset.Value,
                attemptCount);
        }

        public void Dispose()
        {
            _producer.Flush(TimeSpan.FromSeconds(5));
            _producer.Dispose();
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];
    }
}
