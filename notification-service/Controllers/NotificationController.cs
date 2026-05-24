using Microsoft.AspNetCore.Mvc;
using notification_service.DAO;

namespace notification_service.Services
{
    [Route("api/[controller]")]
    [ApiController]
    public class NotificationController : ControllerBase
    {
        private readonly INotificationStreamer _notificationStreamer;
        private readonly INotificationService _notificationService;
        private readonly IMongoDbService _mongoDbService;

        public NotificationController(
            INotificationStreamer notificationStreamer,
            INotificationService notificationService,
            IMongoDbService mongoDbService)
        {
            _notificationStreamer = notificationStreamer;
            _notificationService = notificationService;
            _mongoDbService = mongoDbService;
        }

        [HttpGet("{userId}/stream")]
        public async Task StreamNotifications(string userId, CancellationToken cancellationToken)
        {
            Response.Headers.Append("Content-Type", "text/event-stream");
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");
            var writer = Response.BodyWriter;

            await _notificationStreamer.StreamToClientAsync(userId, writer, cancellationToken);
        }

        [HttpGet("{userId}")]
        public async Task<IActionResult> GetNotifications(string userId, [FromQuery] int limit = 50)
        {
            try
            {
                var notifications = await _mongoDbService.GetNotificationsByUserEmailAsync(userId, limit);
                return Ok(notifications);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new { status = "healthy" });
        }

        [HttpPatch("{notificationId}/read")]
        public async Task<IActionResult> MarkAsRead(string notificationId)
        {
            try
            {
                await _mongoDbService.UpdateNotificationStatusAsync(notificationId, "Read", null);
                return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}