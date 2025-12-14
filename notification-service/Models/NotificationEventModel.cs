using MongoDB.Bson.Serialization.Attributes;

namespace notification_service.Model
{
    public class NotificationEventModel
    {
        [BsonId]
        [BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)]
        public string? Id { get; set; } = string.Empty;

        public string BookingId { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string RecipientEmail { get; set; } = string.Empty;

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public string Message { get; set; } = string.Empty;

        public string Status { get; set; } = "Pending"; // e.g., Pending, Sent, Failed

        public DateTime? SentAt { get; set; }

        public string? ErrorMessage { get; set; }
    }
}