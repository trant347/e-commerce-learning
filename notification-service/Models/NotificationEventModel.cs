using MongoDB.Bson.Serialization.Attributes;

namespace notification_service.Model
{
    public class NotificationEventModel
    {
        [BsonId]
        public string? Id { get; set; }

        public string BookingId { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string RecipientEmail { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public string Message { get; set; } = string.Empty;

        [BsonElement("Status")] // preserve existing Mongo field name; renamed in code to disambiguate from application status
        public string NotificationStatus { get; set; } = "Pending"; // e.g., Pending, Sent, Failed

        public DateTime? SentAt { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>Semantic action identifier — the frontend maps this to a route.</summary>
        public string? ActionType { get; set; }
        /// <summary>Structured data needed by the frontend to build the route.</summary>
        public Dictionary<string, string>? ActionPayload { get; set; }
    }
}