using notification_service.Model;

namespace notification_service.DAO
{
    public interface IMongoDbService
    {
        Task CreateNotificationAsync(NotificationEventModel notificationEvent);
        Task <NotificationEventModel?> GetNotificationByIdAsync(string id);
        Task UpdateNotificationStatusAsync(string id, string status, string? errorMessage);
        Task<List<NotificationEventModel>> GetPendingNotificationsAsync();
        Task<List<NotificationEventModel>> GetNotificationsByUserEmailAsync(string email, int limit = 50);
    }
}