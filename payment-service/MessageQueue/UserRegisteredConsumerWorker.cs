using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using payment_service.Services;

namespace payment_service.MessageQueue
{
    /// <summary>
    /// Subscribes to the cross-service "user-events" topic and creates a wallet (seeded with
    /// UserWallet.DefaultStartingBalance) whenever authorization-service publishes a
    /// USER_REGISTERED event, so every user has funds to spend before their first payment
    /// attempt. Mirrors calendar-service's UserEventConsumerWorker (which cascades
    /// USER_DELETED), consuming the same topic under a different consumer group.
    /// </summary>
    public class UserRegisteredConsumerWorker : BackgroundService
    {
        private static readonly ActivitySource s_activitySource = new("Kafka.Consumer");

        public const string Topic = "user-events";

        private readonly ILogger<UserRegisteredConsumerWorker> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly ConsumerConfig _consumerConfig;

        public UserRegisteredConsumerWorker(
            ILogger<UserRegisteredConsumerWorker> logger,
            IServiceProvider serviceProvider,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;

            var bootstrap = configuration["KafkaConsumerConfig:BootstrapServers"]
                ?? throw new InvalidOperationException("KafkaConsumerConfig:BootstrapServers is required");

            _consumerConfig = new ConsumerConfig
            {
                BootstrapServers = bootstrap,
                GroupId = "payment-service-user-events",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = true,
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();

            using var consumer = new ConsumerBuilder<string, string>(_consumerConfig).Build();
            consumer.Subscribe(Topic);
            _logger.LogInformation("UserRegisteredConsumerWorker started, listening to topic {Topic}", Topic);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = consumer.Consume(stoppingToken);
                        if (result?.Message?.Value == null) continue;

                        using var activity = StartConsumerActivity(result);
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
                _logger.LogInformation("UserRegisteredConsumerWorker is stopping");
            }
            finally
            {
                consumer.Close();
            }
        }

        private async Task HandleAsync(string payload)
        {
            using var scope = _serviceProvider.CreateScope();
            var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();
            await UserRegisteredEventHandler.HandleAsync(payload, wallets, _logger);
        }

        private static Activity? StartConsumerActivity(ConsumeResult<string, string> result)
        {
            ActivityContext parentContext = default;

            if (result.Message?.Headers != null)
            {
                var header = result.Message.Headers.FirstOrDefault(h => h.Key == "traceparent");
                if (header != null)
                {
                    var traceparent = Encoding.UTF8.GetString(header.GetValueBytes());
                    ActivityContext.TryParse(traceparent, null, out parentContext);
                }
            }

            var activity = s_activitySource.StartActivity(
                $"{Topic} process",
                ActivityKind.Consumer,
                parentContext);

            activity?.SetTag("messaging.system", "kafka");
            activity?.SetTag("messaging.destination.name", Topic);
            activity?.SetTag("messaging.operation", "process");

            return activity;
        }
    }
}
