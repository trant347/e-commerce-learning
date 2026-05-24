namespace notification_service.Contracts
{
    public class NotificationMessage
    {
        public string BookingId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string  Message { get; set; } = string.Empty;
        public string RecipientEmail { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        /// <summary>
        /// Used by application-related notifications. Takes precedence over RecipientEmail
        /// when present, since the notification system uses username as the lookup key.
        /// </summary>
        public string? RecipientUsername { get; set; }
        /// <summary>Semantic action identifier — the frontend maps this to a route.</summary>
        public string? ActionType { get; set; }
        /// <summary>Structured data needed by the frontend to build the route (e.g. applicationId, taskMasterId).</summary>
        public Dictionary<string, string>? ActionPayload { get; set; }
    }
}