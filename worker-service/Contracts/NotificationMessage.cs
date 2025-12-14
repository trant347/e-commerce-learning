namespace worker_service.Contracts
{
    public class NotificationMessage
    {
        public string BookingId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // e.g., "Email", "SMS"
        public string Message { get; set; } = string.Empty;
        public string RecipientEmail { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
