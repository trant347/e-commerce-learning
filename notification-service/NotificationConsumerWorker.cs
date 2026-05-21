using System.Text.Json;
using Confluent.Kafka;
using notification_service.Contracts;
using notification_service.Services;

namespace notification_service
{
    public class NotificationConsumerWorker : BackgroundService
    {
        private readonly ILogger<NotificationConsumerWorker> _logger;
        private readonly ConsumerConfig _consumerConfig;
        private readonly IServiceProvider _serviceProvider;
        private readonly List<string> _topics;

        public NotificationConsumerWorker(
            ILogger<NotificationConsumerWorker> logger,
            IServiceProvider serviceProvider,
            ConsumerConfig consumerConfig,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _consumerConfig = consumerConfig;

            // Validate and parse topics
            var topicsSection = configuration.GetSection("KafkaConsumerConfig:Topic");
            if (topicsSection == null || !topicsSection.Exists())
                throw new InvalidOperationException("KafkaConsumerConfig:Topic is required");

            #pragma warning disable CS8601 // Possible null reference assignment.
            _topics = topicsSection.Get<List<string>>();
            #pragma warning restore CS8601 // Possible null reference assignment.

            if (_topics == null || _topics.Count == 0)
                throw new InvalidOperationException("At least one valid topic must be specified in KafkaConsumerConfig:Topic");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Yield immediately so the host can finish startup (web server, etc.)
            // before this background worker blocks the thread with consumer.Consume()
            await Task.Yield();

            using var consumer = new ConsumerBuilder<string, string>(_consumerConfig).Build();
            consumer.Subscribe(_topics);

            _logger.LogInformation("NotificationConsumerWorker started, listening to topics: {Topics}", string.Join(", ", _topics));

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var consumeResult = consumer.Consume(stoppingToken);
                        _logger.LogInformation("Message consumed from topic {Topic}, partition {Partition}, offset {Offset}",
                            consumeResult.Topic, consumeResult.Partition, consumeResult.Offset);

                        using (var scope = _serviceProvider.CreateScope())
                        {
                            var notificationMessage = JsonSerializer.Deserialize<NotificationMessage>(consumeResult.Message.Value);
                            if (notificationMessage == null)
                            {
                                _logger.LogWarning("Received null or invalid notification message");
                                continue;
                            }

                            _logger.LogInformation("Processing notification for RecipientEmail: {RecipientEmail}", notificationMessage.RecipientEmail);

                            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
                            var mongoDbService = scope.ServiceProvider.GetRequiredService<DAO.IMongoDbService>();
                            
                            _logger.LogInformation("Processing message: {Message}", consumeResult.Message.Value);

                            var notification = new Model.NotificationEventModel
                            {
                                Id = Guid.NewGuid().ToString(),
                                // RecipientUsername takes precedence — the notification system uses
                                // username as the lookup key (stored in RecipientEmail for backward compat)
                                RecipientEmail = !string.IsNullOrEmpty(notificationMessage.RecipientUsername)
                                    ? notificationMessage.RecipientUsername
                                    : notificationMessage.RecipientEmail,
                                Status = "Pending",
                                Timestamp = notificationMessage.Timestamp,
                                Message = notificationMessage.Message,
                                Type = notificationMessage.Type,
                                ActionUrl = notificationMessage.ActionUrl,
                            };

                            await mongoDbService.CreateNotificationAsync(notification);
                            await notificationService.SendNotificationAsync(notification);
                        }
                    }
                    catch (ConsumeException ex)
                    {
                        _logger.LogError(ex, "Error consuming message from Kafka");
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Error deserializing message");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("NotificationConsumerWorker is stopping");
            }
            finally
            {
                consumer.Close();
                _logger.LogInformation("NotificationConsumerWorker closed");
            }
        }
    }
}