using notification_service.DAO;
using notification_service.Model;

namespace notification_service.Services
{
    public class NotificationService : INotificationService
    {
        private readonly ILogger<NotificationService> _logger;
        private readonly IMongoDbService _mongoDbService;
        private readonly INotificationStreamer _notificationStreamer;

        public NotificationService(
            ILogger<NotificationService> logger, 
            IMongoDbService mongoDbService,
            INotificationStreamer notificationStreamer)
        {
            _logger = logger;
            _mongoDbService = mongoDbService;
            _notificationStreamer = notificationStreamer;
        }

        public async Task SendNotificationAsync(NotificationEventModel notificationEvent)
        {
            // Implementation for sending notification
            _logger.LogInformation("Sending notification to {RecipientEmail}", notificationEvent.RecipientEmail);

            // Push to connected clients via SSE
            await _notificationStreamer.SendNotificationAsync(
                notificationEvent.RecipientEmail, 
                new NotificationStreamedEvent
                {
                    Id = notificationEvent.Id,
                    BookingId = notificationEvent.BookingId,
                    Type = notificationEvent.Type,
                    Message = notificationEvent.Message,
                    Timestamp = notificationEvent.Timestamp,
                    NotificationStatus = notificationEvent.NotificationStatus,
                    ActionType = notificationEvent.ActionType,
                    ActionPayload = notificationEvent.ActionPayload,
                });

            _logger.LogInformation("Notification sent to {RecipientEmail}", notificationEvent.RecipientEmail);
        }
    }
}