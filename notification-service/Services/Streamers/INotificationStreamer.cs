using System.IO.Pipelines;

namespace notification_service.Services
{
    public interface INotificationStreamer
    {
        Task StreamToClientAsync(string userId, PipeWriter writer, CancellationToken cancellationToken);
        Task SendNotificationAsync(string userId,  NotificationStreamedEvent message);
    }
}   