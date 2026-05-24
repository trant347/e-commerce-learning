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
        /// <summary>Semantic action identifier — the frontend maps this to a route.</summary>
        public string? ActionType { get; set; }
        /// <summary>Structured data needed by the frontend to build the route.</summary>
        public Dictionary<string, string>? ActionPayload { get; set; }
    }
}