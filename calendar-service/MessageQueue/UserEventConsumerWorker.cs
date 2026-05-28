using System.Text.Json;
using calendar_service.Services.Contracts;
using Confluent.Kafka;

namespace calendar_service.MessageQueue
{
    /// <summary>
    /// Subscribes to the cross-service "user-events" topic and cascades USER_DELETED
    /// events into the booking collection. Mirrors product-service's UserEventConsumer
    /// so a deleted user leaves no orphaned bookings (either as requester or owner).
    /// </summary>
    public class UserEventConsumerWorker : BackgroundService
    {
        public const string Topic = "user-events";
        private const string UserDeletedType = "USER_DELETED";

        private readonly ILogger<UserEventConsumerWorker> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConsumerConfig _consumerConfig;

        public UserEventConsumerWorker(
            ILogger<UserEventConsumerWorker> logger,
            IServiceProvider serviceProvider,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;

            var bootstrap = configuration["KafkaProducerConfig:BootstrapServers"]
                ?? throw new InvalidOperationException("KafkaProducerConfig:BootstrapServers is required");

            _consumerConfig = new ConsumerConfig
            {
                BootstrapServers = bootstrap,
                GroupId = "calendar-service-user-events",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = true,
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();

            using var consumer = new ConsumerBuilder<string, string>(_consumerConfig).Build();
            consumer.Subscribe(Topic);
            _logger.LogInformation("UserEventConsumerWorker started, listening to topic {Topic}", Topic);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = consumer.Consume(stoppingToken);
                        if (result?.Message?.Value == null) continue;

                        await HandleAsync(result.Message.Value);
                    }
                    catch (ConsumeException ex)
                    {
                        _logger.LogError(ex, "Error consuming user-event");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing user-event");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("UserEventConsumerWorker is stopping");
            }
            finally
            {
                consumer.Close();
            }
        }

        private async Task HandleAsync(string payload)
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, UserDeletedType, StringComparison.Ordinal))
            {
                _logger.LogDebug("Ignoring user-event of type '{Type}'", type);
                return;
            }

            var username = root.TryGetProperty("username", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(username))
            {
                _logger.LogWarning("USER_DELETED event missing 'username': {Payload}", payload);
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var bookings = scope.ServiceProvider.GetRequiredService<IBookingService>();
            var deleted = await bookings.DeleteForUserAsync(username);

            _logger.LogInformation("USER_DELETED cascade for '{Username}': bookings={Count}", username, deleted);
        }
    }
}
