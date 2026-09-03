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
        public const string StatusInProgress = "IN_PROGRESS";
        public const string StatusDeclined = "DECLINED";
        public const string StatusCancelled = "CANCELLED";

        /// <summary>The TaskMaster has uploaded proof of the completed job and sent an invoice; awaiting payment.</summary>
        public const string StatusImplemented = "IMPLEMENTED";

        /// <summary>The requester has paid the invoice. Terminal state.</summary>
        public const string StatusCompleted = "COMPLETED";

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

        /// <summary>
        /// Immutable booking price copied from the accepted offer before escrow funding.
        /// The asynchronous /pay flow must use this server-side value, never a client amount.
        /// </summary>
        public decimal? AgreedAmount { get; set; }

        public string? AgreedCurrency { get; set; }

        /// <summary>Stable payment-service escrow id for the asynchronous payment flow.</summary>
        public Guid? EscrowId { get; set; }

        /// <summary>
        /// Read-only projection of payment-service's authoritative escrow status. It is updated
        /// only from verified payment results and must not be treated as the financial ledger.
        /// </summary>
        public string? EscrowStatus { get; set; }

        public string Status { get; set; } = StatusPending;

        public string? RequestMessage { get; set; }
        public string? ResponseMessage { get; set; }

        /// <summary>
        /// URL of the proof-of-job file/image the TaskMaster uploaded when submitting the
        /// invoice (see <see cref="StatusImplemented"/>). Null until submitted.
        /// </summary>
        public string? ProofFileUrl { get; set; }

        /// <summary>
        /// Amount the TaskMaster is invoicing the requester for. Defaults to
        /// <see cref="OfferedTotalAmount"/> at submission time but the TaskMaster may
        /// adjust it. Null until the TaskMaster submits proof of the job.
        /// </summary>
        public decimal? InvoiceAmount { get; set; }

        /// <summary>Id of the approved payment-service transaction once paid. Null until paid.</summary>
        public string? PaymentTransactionId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RespondedAt { get; set; }

        /// <summary>When the TaskMaster submitted proof of job + invoice (moved to IMPLEMENTED).</summary>
        public DateTime? ImplementedAt { get; set; }

        public DateTime? WorkStartedAt { get; set; }
        public DateTime? ReleaseRequestedAt { get; set; }
        public DateTime? RefundRequestedAt { get; set; }
        public DateTime? CancelledAt { get; set; }

        /// <summary>When the requester paid the invoice (moved to COMPLETED).</summary>
        public DateTime? CompletedAt { get; set; }

        public void FixAgreedPrice(string currency = "USD")
        {
            if (OfferedTotalAmount is null or <= 0)
            {
                throw new InvalidOperationException("A positive offered price is required before acceptance");
            }
            if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
            {
                throw new InvalidOperationException("Currency must be a three-letter code");
            }

            AgreedAmount = Math.Round(OfferedTotalAmount.Value, 2, MidpointRounding.ToEven);
            AgreedCurrency = currency.Trim().ToUpperInvariant();
        }
    }
}
