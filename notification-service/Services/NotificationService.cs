using notification_service.DAO;
using notification_service.Model;

namespace notification_service.Services
{
    public class NotificationService : INotificationService
    {
        private readonly ILogger<NotificationService> _logger;
        private readonly IMongoDbService _mongoDbService;

        public NotificationService(ILogger<NotificationService> logger, IMongoDbService mongoDbService)
        {
            _logger = logger;
            _mongoDbService = mongoDbService;
        }

        public async Task SendNotificationAsync(NotificationEventModel notificationEvent)
        {
            // Implementation for sending notification
            _logger.LogInformation("Sending notification to {RecipientEmail}", notificationEvent.RecipientEmail);

            // Simulate sending notification
            await Task.Delay(1000);

            _logger.LogInformation("Notification sent to {RecipientEmail}", notificationEvent.RecipientEmail);
        }
    }
}