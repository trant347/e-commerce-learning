using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace calendar_service.Model
{
    /// <summary>
    /// A booking raised by a user against a TaskMaster for one or more consecutive
    /// hour-aligned slots starting at SlotStart and lasting DurationHours hours.
    /// Multiple PENDING bookings may overlap; when the TaskMaster accepts one, all
    /// other PENDING bookings whose range overlaps are auto-declined.
    /// </summary>
    public class Booking
    {
        public const string StatusPending = "PENDING";
        public const string StatusAccepted = "ACCEPTED";
        public const string StatusDeclined = "DECLINED";
        public const string StatusCancelled = "CANCELLED";

        public const int MaxDurationHours = 24;

        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public string TaskMasterId { get; set; } = string.Empty;
        public string TaskMasterUsername { get; set; } = string.Empty;
        public string RequesterUsername { get; set; } = string.Empty;

        /// <summary>UTC start of the first hour-aligned slot.</summary>
        public DateTime SlotStart { get; set; }

        /// <summary>Number of consecutive 1-hour slots. >= 1.</summary>
        public int DurationHours { get; set; } = 1;

        [BsonIgnore]
        public DateTime SlotEnd => SlotStart.AddHours(DurationHours);

        /// <summary>
        /// Hourly rate the requester is offering to pay, set by the requester at booking
        /// time. Visible to the TaskMaster reviewing the request. Null for legacy bookings
        /// created before this field existed.
        /// </summary>
        public decimal? OfferedRatePerHour { get; set; }

        /// <summary>Total amount offered for the whole slot (OfferedRatePerHour * DurationHours).</summary>
        [BsonIgnore]
        public decimal? OfferedTotalAmount => OfferedRatePerHour.HasValue ? OfferedRatePerHour.Value * DurationHours : null;

        public string Status { get; set; } = StatusPending;

        public string? RequestMessage { get; set; }
        public string? ResponseMessage { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RespondedAt { get; set; }
    }
}
