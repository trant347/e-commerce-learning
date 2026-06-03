using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using worker_service.Contracts;

namespace worker_service.MessageQueue
{
    public class NotificationProducer : INotificationProducer<string, BookingJobStatusMessage>
    {
        private static readonly ActivitySource s_activitySource = new("Kafka.Producer");

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
                var kafkaMessage = new Message<string, BookingJobStatusMessage>
                {
                    Key = key,
                    Value = message
                };

                using var activity = s_activitySource.StartActivity($"{topic} publish", ActivityKind.Producer);
                activity?.SetTag("messaging.system", "kafka");
                activity?.SetTag("messaging.destination.name", topic);

                if (Activity.Current != null)
                {
                    kafkaMessage.Headers = new Headers();
                    kafkaMessage.Headers.Add("traceparent", Encoding.UTF8.GetBytes(Activity.Current.Id!));
                }

                await _producer.ProduceAsync(topic, kafkaMessage);
            }
        }

        public void Dispose()
        {
            _producer?.Dispose();
        }
    }
}
