namespace notification_service.Services
{
    public class NotificationStreamedEvent
    {
        public string? Id { get; set; }
        public string BookingId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}