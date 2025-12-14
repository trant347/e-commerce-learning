using Confluent.Kafka;
using worker_service.Contracts;

namespace worker_service.MessageQueue
{
    public class NotificationProducer : INotificationProducer<string, BookingJobStatusMessage>
    {
        private readonly List<string> _topics;
        private readonly IProducer<string, BookingJobStatusMessage> _producer;

        public NotificationProducer(List<string> topics, IProducer<string, BookingJobStatusMessage> producer)
        {
            _topics = topics;
            _producer = producer;
        }

        public async Task ProduceNotificationAsync(string key, BookingJobStatusMessage message, CancellationToken token)
        {
            foreach (var topic in _topics)
            {
                await _producer.ProduceAsync(topic, new Message<string, BookingJobStatusMessage>
                {
                    Key = key,
                    Value = message
                });
            }
        }

        public void Dispose()
        {
            _producer?.Dispose();
        }
    }
}
