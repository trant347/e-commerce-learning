using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace calendar_service.MessageQueue
{
    /// <summary>
    /// Publishes JSON messages to the notification-events Kafka topic that notification-service
    /// consumes. Payload shape mirrors notification_service.Contracts.NotificationMessage:
    ///   { type, recipientUsername, message, actionType, actionPayload }.
    /// </summary>
    public class NotificationProducer : INotificationProducer, IDisposable
    {
        private static readonly ActivitySource s_activitySource = new("Kafka.Producer");

        private readonly IProducer<string, string> _producer;
        private readonly string _topic;
        private readonly ILogger<NotificationProducer> _logger;

        public NotificationProducer(IOptions<KafkaProducerConfig> config, ILogger<NotificationProducer> logger)
        {
            var cfg = config.Value;
            _topic = string.IsNullOrEmpty(cfg.NotificationTopic) ? "notification-events" : cfg.NotificationTopic;
            _logger = logger;
            var producerConfig = new ProducerConfig
            {
                BootstrapServers = cfg.BootstrapServers,
                ClientId = (cfg.ClientId ?? "calendar-service") + "-notify"
            };
            _producer = new ProducerBuilder<string, string>(producerConfig).Build();
        }

        public async Task PublishAsync(object payload)
        {
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var message = new Message<string, string> { Value = json };

            using var activity = s_activitySource.StartActivity($"{_topic} publish", ActivityKind.Producer);
            activity?.SetTag("messaging.system", "kafka");
            activity?.SetTag("messaging.destination.name", _topic);

            if (Activity.Current != null)
            {
                message.Headers = new Headers();
                message.Headers.Add("traceparent", Encoding.UTF8.GetBytes(Activity.Current.Id!));
            }

            _logger.LogInformation("[NotificationProducer] -> {Topic} {Payload}", _topic, json);
            await _producer.ProduceAsync(_topic, message);
        }

        public void Dispose()
        {
            _producer?.Flush(TimeSpan.FromSeconds(2));
            _producer?.Dispose();
        }
    }
}
