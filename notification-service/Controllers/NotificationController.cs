using Microsoft.AspNetCore.Mvc;

namespace notification_service.Services
{
    [Route("api/[controller]")]
    [ApiController]
    public class NotificationController : ControllerBase
    {
        private readonly INotificationStreamer _notificationStreamer;

        public NotificationController(INotificationStreamer notificationStreamer)
        {
            _notificationStreamer = notificationStreamer;
        }

        [HttpGet("{userId: string}/stream")]
        public async Task StreamNotifications(string userId, CancellationToken cancellationToken)
        {
            Response.Headers.Append("Content-Type", "text/event-stream");
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");
            var writer = Response.BodyWriter;

            await _notificationStreamer.StreamToClientAsync(userId, writer, cancellationToken);
        }
    }
}