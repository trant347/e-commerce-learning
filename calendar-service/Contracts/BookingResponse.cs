using calendar_service.Model;
using Payment.Contracts.V1;

namespace calendar_service.Contracts
{
    /// <summary>
    /// Wire representation of a booking. It exists so read-model concerns — most importantly the
    /// latest payment-saga projection the frontend needs to survive a page reload — stay out of
    /// the persisted <see cref="Booking"/> document instead of riding along on it as
    /// <c>[BsonIgnore]</c> properties that only one endpoint ever fills in.
    /// </summary>
    /// <remarks>
    /// The serialized shape must stay a superset of <see cref="Booking"/>'s. BookingResponseTests
    /// asserts that, so a field added to the entity cannot silently vanish from the API.
    /// </remarks>
    public sealed class BookingResponse
    {
        public string? Id { get; set; }

        public string TaskMasterId { get; set; } = string.Empty;
        public string TaskMasterUsername { get; set; } = string.Empty;
        public string RequesterUsername { get; set; } = string.Empty;

        public DateTime SlotStart { get; set; }
        public int DurationHours { get; set; }
        public DateTime SlotEnd { get; set; }

        public decimal? OfferedRatePerHour { get; set; }
        public decimal? OfferedTotalAmount { get; set; }
        public decimal? AgreedAmount { get; set; }
        public string? AgreedCurrency { get; set; }

        public Guid? EscrowId { get; set; }
        public string? EscrowStatus { get; set; }

        public string Status { get; set; } = string.Empty;

        public string? RequestMessage { get; set; }
        public string? ResponseMessage { get; set; }
        public string? ProofFileUrl { get; set; }
        public decimal? InvoiceAmount { get; set; }
        public string? PaymentTransactionId { get; set; }

        /// <summary>
        /// True while this booking's most recent payment saga is still STARTED, i.e. the server
        /// has not established whether a prior charge succeeded. The frontend uses it to block a
        /// duplicate /pay attempt across reloads. See PAYMENT_SAGA_SPEC.md.
        /// </summary>
        public bool PaymentPending { get; set; }

        public Guid? LatestPaymentSagaId { get; set; }
        public string? LatestPaymentStatus { get; set; }
        public string? LatestPaymentOperation { get; set; }
        public string? LatestPaymentFailureReason { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? RespondedAt { get; set; }
        public DateTime? ImplementedAt { get; set; }
        public DateTime? WorkStartedAt { get; set; }
        public DateTime? ReleaseRequestedAt { get; set; }
        public DateTime? RefundRequestedAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public static BookingResponse From(Booking booking) => new()
        {
            Id = booking.Id,
            TaskMasterId = booking.TaskMasterId,
            TaskMasterUsername = booking.TaskMasterUsername,
            RequesterUsername = booking.RequesterUsername,
            SlotStart = booking.SlotStart,
            DurationHours = booking.DurationHours,
            SlotEnd = booking.SlotEnd,
            OfferedRatePerHour = booking.OfferedRatePerHour,
            OfferedTotalAmount = booking.OfferedTotalAmount,
            AgreedAmount = booking.AgreedAmount,
            AgreedCurrency = booking.AgreedCurrency,
            EscrowId = booking.EscrowId,
            EscrowStatus = booking.EscrowStatus,
            Status = booking.Status,
            RequestMessage = booking.RequestMessage,
            ResponseMessage = booking.ResponseMessage,
            ProofFileUrl = booking.ProofFileUrl,
            InvoiceAmount = booking.InvoiceAmount,
            PaymentTransactionId = booking.PaymentTransactionId,
            CreatedAt = booking.CreatedAt,
            RespondedAt = booking.RespondedAt,
            ImplementedAt = booking.ImplementedAt,
            WorkStartedAt = booking.WorkStartedAt,
            ReleaseRequestedAt = booking.ReleaseRequestedAt,
            RefundRequestedAt = booking.RefundRequestedAt,
            CancelledAt = booking.CancelledAt,
            CompletedAt = booking.CompletedAt
        };

        public static List<BookingResponse> FromMany(IEnumerable<Booking> bookings) =>
            bookings.Select(From).ToList();

        /// <summary>
        /// Overlays the booking's most recent durable saga so a reloading client can resume
        /// polling. A saga without an escrow/operation is a legacy row and contributes only
        /// <see cref="PaymentPending"/>.
        /// </summary>
        public BookingResponse WithLatestPayment(SagaState? saga)
        {
            if (saga == null) return this;

            PaymentPending = saga.Status == SagaState.StatusStarted;
            if (!saga.EscrowId.HasValue || string.IsNullOrWhiteSpace(saga.Operation))
            {
                return this;
            }

            LatestPaymentSagaId = saga.SagaId;
            LatestPaymentStatus = saga.Status == SagaState.StatusStarted
                ? PaymentStatusResponseV1.PendingStatus
                : saga.Status;
            LatestPaymentOperation = saga.Operation;
            LatestPaymentFailureReason = saga.FailureReason;
            return this;
        }
    }
}
