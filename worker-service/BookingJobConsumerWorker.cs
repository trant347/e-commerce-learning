using Confluent.Kafka;
using System.Text.Json;
using worker_service.Contracts;
using worker_service.MessageQueue;
using worker_service.Services;

namespace worker_service
{
    public class BookingJobConsumerWorker : BackgroundService
    {
        private readonly ILogger<BookingJobConsumerWorker> _logger;
        private readonly IConfiguration _configuration;
        private readonly ConsumerConfig _consumerConfig;
        private readonly IProcessBookingService _processBookingService;
        private readonly INotificationProducer<string, BookingJobStatusMessage> _notificationProducer;

        public BookingJobConsumerWorker(
            ILogger<BookingJobConsumerWorker> logger,
            IConfiguration configuration,
            IProcessBookingService processBookingService,
            INotificationProducer<string, BookingJobStatusMessage> notificationProducer
            )
        {
            _logger = logger;
            _configuration = configuration;
            _processBookingService = processBookingService;
            _notificationProducer = notificationProducer;
            _consumerConfig = new ConsumerConfig
            {
                BootstrapServers = _configuration["Kafka:BootstrapServers"],
                GroupId = _configuration["Kafka:GroupId"],
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false
            };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Booking Job Consumer Worker starting at: {time}", DateTimeOffset.Now);
            
            using var consumer = new ConsumerBuilder<string, BookingJobMessage>(_consumerConfig)
                .SetKeyDeserializer(Deserializers.Utf8)
                .SetValueDeserializer(new BookingJobMessageDeserializer<BookingJobMessage>())
                .Build();
            
            var topics = _configuration.GetSection("Kafka:Topics").Get<List<string>>();
            if (topics == null || topics.Count == 0)
            {
                _logger.LogError("No topics configured for Booking Job Consumer.");
                return;
            }
            consumer.Subscribe(topics);
            _logger.LogInformation($"Subscribed to topics: {topics}", string.Join(", ", topics));

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var consumeResult = consumer.Consume(stoppingToken);
                        if (consumeResult != null)
                        {
                            var bookingJob = consumeResult.Message.Value;
                            _logger.LogInformation($"Received booking job: {bookingJob.Id}");
                            // Process the booking job
                            await _processBookingService.ProcessBookingAsync(bookingJob);
                            // Send notification after processing
                            await _notificationProducer.ProduceNotificationAsync(bookingJob.Id,
                                new BookingJobStatusMessage() { 
                                    BookingId = bookingJob.UserId, 
                                     Status = "Processed" }, 
                                stoppingToken);
                            _logger.LogInformation($"Notification sent for booking job: {bookingJob.Id}");
                            // Manually commit the offset after successful processing
                            consumer.Commit(consumeResult);
                        }
                    }
                    catch (ConsumeException ex)
                    {
                        _logger.LogError(ex, $"Error consuming message: {ex.Error.Reason}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error occurred while processing booking job.");
                    }
                }

            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Booking Job Consumer Worker is stopping due to cancellation.");
            }
            finally
            {
                consumer.Close();
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Booking Job Consumer Worker stopping at: {time}", DateTimeOffset.Now);
            await base.StopAsync(stoppingToken);
        }
    }
}
