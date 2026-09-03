using System.Diagnostics;
using calendar_service.Model;
using calendar_service.Services.Contracts;
using Payment.Contracts.V1;

namespace calendar_service.Services.Implementation
{
    /// <inheritdoc cref="IEscrowPaymentService"/>
    public sealed class EscrowPaymentService : IEscrowPaymentService
    {
        private readonly IBookingService _bookings;
        private readonly ISagaStateService _sagaStateService;
        private readonly IConfiguration _configuration;

        public EscrowPaymentService(
            IBookingService bookings,
            ISagaStateService sagaStateService,
            IConfiguration configuration)
        {
            _bookings = bookings;
            _sagaStateService = sagaStateService;
            _configuration = configuration;
        }

        public async Task<PaymentAcceptedResponseV1> FundEscrowAsync(
            string bookingId,
            string callerUsername,
            string paymentMethodToken,
            CancellationToken cancellationToken = default)
        {
            var booking = await _bookings.GetByIdAsync(bookingId)
                ?? throw new KeyNotFoundException($"Booking '{bookingId}' was not found");

            if (!string.Equals(
                    booking.RequesterUsername,
                    callerUsername,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    "Only the requester may fund escrow for this booking");
            }
            if (booking.Status != Booking.StatusAccepted)
            {
                throw new InvalidOperationException(
                    $"Booking is {booking.Status} and is not eligible for escrow funding");
            }
            if (booking.AgreedAmount is null or <= 0
                || string.IsNullOrWhiteSpace(booking.AgreedCurrency))
            {
                throw new InvalidOperationException(
                    "Booking price and currency must be fixed before escrow funding");
            }
            if (booking.WorkStartedAt.HasValue)
            {
                throw new InvalidOperationException(
                    "Work has already started for this booking");
            }
            if (booking.EscrowId.HasValue
                && booking.EscrowStatus != EscrowStatus.Pending)
            {
                throw new InvalidOperationException(
                    $"Booking escrow is {booking.EscrowStatus} and cannot be funded again");
            }

            var latestSaga = await _sagaStateService.GetLatestByBookingIdAsync(bookingId);
            if (latestSaga?.Status == SagaState.StatusStarted)
            {
                throw new ActivePaymentSagaException(
                    bookingId,
                    PaymentOperation.FundEscrow,
                    InProgressMessage(PaymentOperation.FundEscrow));
            }

            // A retried funding attempt must reuse the escrow already attached to the booking so
            // payment-service keeps treating it as one escrow rather than opening a second one.
            var escrowId = booking.EscrowId ?? Guid.NewGuid();
            if (!booking.EscrowId.HasValue)
            {
                booking = await _bookings.AttachEscrowAsync(bookingId, callerUsername, escrowId);
            }

            var request = new PaymentRequestedV1
            {
                SagaId = Guid.NewGuid(),
                EscrowId = escrowId,
                BookingId = bookingId,
                Operation = PaymentOperation.FundEscrow,
                Amount = booking.AgreedAmount!.Value,
                Currency = booking.AgreedCurrency!,
                PayerUserId = callerUsername,
                PayeeUserId = RequireCustodyUserId(),
                TaskMasterUserId = booking.TaskMasterUsername,
                PaymentMethodToken = paymentMethodToken.Trim()
            };

            return await EnqueueAsync(request, cancellationToken);
        }

        public async Task<PaymentAcceptedResponseV1> EnqueueTransferAsync(
            Booking booking,
            string operation,
            CancellationToken cancellationToken = default)
        {
            if (!booking.EscrowId.HasValue
                || booking.AgreedAmount is null or <= 0
                || string.IsNullOrWhiteSpace(booking.AgreedCurrency))
            {
                throw new InvalidOperationException(
                    "Booking escrow, fixed amount, and currency are required");
            }

            var custodyUserId = RequireCustodyUserId();
            var payeeUserId = operation switch
            {
                PaymentOperation.ReleaseEscrow => booking.TaskMasterUsername,
                PaymentOperation.RefundEscrow => booking.RequesterUsername,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(operation),
                    operation,
                    "Only release and refund transfers can be enqueued here.")
            };

            var request = new PaymentRequestedV1
            {
                SagaId = Guid.NewGuid(),
                EscrowId = booking.EscrowId.Value,
                BookingId = booking.Id
                    ?? throw new InvalidOperationException("Booking id is required"),
                Operation = operation,
                Amount = booking.AgreedAmount.Value,
                Currency = booking.AgreedCurrency,
                PayerUserId = custodyUserId,
                PayeeUserId = payeeUserId,
                TaskMasterUserId = booking.TaskMasterUsername
            };

            return await EnqueueAsync(request, cancellationToken);
        }

        public async Task EnsureNoActiveOperationAsync(string bookingId, string operation)
        {
            var latest = await _sagaStateService.GetLatestByBookingIdAsync(bookingId);
            if (latest?.Status == SagaState.StatusStarted && latest.Operation == operation)
            {
                throw new ActivePaymentSagaException(
                    bookingId,
                    operation,
                    InProgressMessage(operation));
            }
        }

        private static string InProgressMessage(string operation) => operation switch
        {
            PaymentOperation.FundEscrow =>
                "Escrow funding for this booking is already being processed",
            PaymentOperation.ReleaseEscrow =>
                "Escrow release for this booking is already being processed",
            PaymentOperation.RefundEscrow =>
                "Escrow refund for this booking is already being processed",
            _ => $"An active {operation} operation for this booking is already being processed"
        };

        private async Task<PaymentAcceptedResponseV1> EnqueueAsync(
            PaymentRequestedV1 request,
            CancellationToken cancellationToken)
        {
            await _sagaStateService.EnqueueAsync(
                request,
                Activity.Current?.Id,
                cancellationToken);

            return new PaymentAcceptedResponseV1
            {
                SagaId = request.SagaId,
                EscrowId = request.EscrowId,
                StatusUrl = PaymentStatusUrl(request.SagaId)
            };
        }

        /// <summary>
        /// Path the client polls for the durable saga outcome. Kept beside the enqueue logic so
        /// every accepted payment response advertises the same status resource.
        /// </summary>
        public static string PaymentStatusUrl(Guid sagaId) =>
            $"/api/booking/payment-status/{sagaId:D}";

        private string RequireCustodyUserId()
        {
            var custodyUserId = _configuration["Escrow:CustodyUserId"];
            if (string.IsNullOrWhiteSpace(custodyUserId))
            {
                throw new EscrowConfigurationException("Escrow:CustodyUserId is required");
            }
            return custodyUserId;
        }
    }
}
