using Confluent.Kafka;
using worker_service.Contracts;

namespace worker_service.MessageQueue
{
    public static class CreateKafkaProducer
    {
        public static INotificationProducer<string, BookingJobStatusMessage> CreateProducer(KafkaProducerConfig config)
        {
            var producerConfig = new Confluent.Kafka.ProducerConfig
            {
                BootstrapServers = config.BootstrapServers,
                ClientId = config.ClientId
            };
            var producer = new ProducerBuilder<string, BookingJobStatusMessage>(producerConfig)
                .SetValueSerializer(new NotificationStatusSerializer<BookingJobStatusMessage>())
                .Build();

            return new NotificationProducer(config.OutputTopics, producer);
        }
    }
}
