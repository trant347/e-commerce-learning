using notification_service.Model;

namespace notification_service.Services
{
    public interface INotificationService
    {
        Task SendNotificationAsync(NotificationEventModel notificationEvent);
    }
}