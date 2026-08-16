using calendar_service.Model;
using calendar_service.Services;
using calendar_service.Services.Contracts;
using Payment.Contracts.V1;

namespace calendar_service.MessageQueue
{
    public sealed class PaymentResultProcessor : IPaymentResultProcessor
    {
        private readonly ISagaStateService _sagaState;
        private readonly IBookingService _bookings;
        private readonly INotificationProducer _notifications;
        private readonly ILogger<PaymentResultProcessor> _logger;

        public PaymentResultProcessor(
            ISagaStateService sagaState,
            IBookingService bookings,
            INotificationProducer notifications,
            ILogger<PaymentResultProcessor> logger)
        {
            _sagaState = sagaState;
            _bookings = bookings;
            _notifications = notifications;
            _logger = logger;
        }

        public async Task<PaymentResultProcessingOutcome> ProcessAsync(
            PaymentResultV1 result,
            CancellationToken cancellationToken = default)
        {
            ValidateResult(result);

            var saga = await _sagaState.GetBySagaIdAsync(result.SagaId)
                ?? throw new PaymentResultRetryableException(
                    $"Saga {result.SagaId:D} is not available yet.");

            if (saga.Status != SagaState.StatusStarted)
            {
                if (IsExactTerminalDuplicate(saga, result))
                {
                    _logger.LogInformation(
                        "Ignoring duplicate payment result sagaId={SagaId} transactionId={TransactionId}",
                        result.SagaId,
                        result.TransactionId);
                    return PaymentResultProcessingOutcome.Duplicate;
                }

                throw new InvalidOperationException(
                    $"Saga {result.SagaId:D} is already {saga.Status} with a different result.");
            }

            var mismatch = FindMismatch(saga, result);
            if (mismatch != null)
            {
                var failed = await _sagaState.FailResultAsync(
                    result.SagaId,
                    result.TransactionId.ToString("D"),
                    mismatch,
                    cancellationToken);
                if (!failed)
                {
                    return await ResolveConcurrentTerminalResultAsync(result);
                }
                _logger.LogWarning(
                    "Rejected mismatched payment result sagaId={SagaId}: {Reason}",
                    result.SagaId,
                    mismatch);
                return PaymentResultProcessingOutcome.Mismatched;
            }

            if (result.Status == PaymentResultV1.StatusDeclined)
            {
                var reason = string.IsNullOrWhiteSpace(result.DeclineReason)
                    ? $"{result.Operation} was declined."
                    : result.DeclineReason.Trim();
                var failed = await _sagaState.FailResultAsync(
                    result.SagaId,
                    result.TransactionId.ToString("D"),
                    reason,
                    cancellationToken);
                if (!failed)
                {
                    return await ResolveConcurrentTerminalResultAsync(result);
                }
                return PaymentResultProcessingOutcome.Declined;
            }

            var application = await _bookings.ApplyApprovedPaymentResultAsync(
                result,
                cancellationToken);
            if (application.Outcome == PaymentResultApplicationOutcome.Applied)
            {
                await PublishNotificationsAsync(
                    result,
                    application.Booking);
            }

            var completed = await _sagaState.CompleteResultAsync(
                result.SagaId,
                result.TransactionId.ToString("D"),
                cancellationToken);
            if (!completed)
            {
                var current = await _sagaState.GetBySagaIdAsync(result.SagaId);
                if (current?.Status != SagaState.StatusCompleted
                    || current.PaymentTransactionId != result.TransactionId.ToString("D"))
                {
                    throw new PaymentResultRetryableException(
                        $"Saga {result.SagaId:D} changed while its result was being applied.");
                }
            }

            return PaymentResultProcessingOutcome.Applied;
        }

        private async Task<PaymentResultProcessingOutcome>
            ResolveConcurrentTerminalResultAsync(PaymentResultV1 result)
        {
            var current = await _sagaState.GetBySagaIdAsync(result.SagaId);
            if (current != null && IsExactTerminalDuplicate(current, result))
            {
                return PaymentResultProcessingOutcome.Duplicate;
            }

            throw new PaymentResultRetryableException(
                $"Saga {result.SagaId:D} changed while its result was being resolved.");
        }

        private async Task PublishNotificationsAsync(
            PaymentResultV1 result,
            Booking booking)
        {
            var actionPayload = new Dictionary<string, string>
            {
                ["bookingId"] = booking.Id ?? result.BookingId,
                ["taskMasterId"] = booking.TaskMasterId
            };

            switch (result.Operation)
            {
                case PaymentOperation.FundEscrow:
                    await _notifications.PublishAsync(new BookingNotification(
                        "BOOKING_ESCROW_FUNDED",
                        booking.RequesterUsername,
                        $"Your payment of {result.Amount:0.00} {result.Currency} is held in escrow. Work may begin.",
                        "VIEW_OUTGOING_BOOKING_REQUEST",
                        actionPayload));
                    await _notifications.PublishAsync(new BookingNotification(
                        "BOOKING_ESCROW_FUNDED",
                        booking.TaskMasterUsername,
                        $"{booking.RequesterUsername} funded escrow with {result.Amount:0.00} {result.Currency}. Work may begin.",
                        "VIEW_INCOMING_BOOKING_REQUEST",
                        actionPayload));
                    break;

                case PaymentOperation.ReleaseEscrow:
                    await _notifications.PublishAsync(new BookingNotification(
                        "BOOKING_ESCROW_RELEASED",
                        booking.TaskMasterUsername,
                        $"Escrow funds of {result.Amount:0.00} {result.Currency} were paid for the completed booking.",
                        "VIEW_INCOMING_BOOKING_REQUEST",
                        actionPayload));
                    await _notifications.PublishAsync(new BookingNotification(
                        "BOOKING_COMPLETED",
                        booking.RequesterUsername,
                        $"Your booking is complete. Escrow funds of {result.Amount:0.00} {result.Currency} were released to {booking.TaskMasterUsername}.",
                        "VIEW_BOOKING_DETAILS",
                        actionPayload));
                    break;

                case PaymentOperation.RefundEscrow:
                    await _notifications.PublishAsync(new BookingNotification(
                        "BOOKING_ESCROW_REFUNDED",
                        booking.RequesterUsername,
                        $"Your escrow payment of {result.Amount:0.00} {result.Currency} was refunded.",
                        "VIEW_OUTGOING_BOOKING_REQUEST",
                        actionPayload));
                    break;
            }
        }

        private static bool IsExactTerminalDuplicate(
            SagaState saga,
            PaymentResultV1 result) =>
            saga.Status is SagaState.StatusCompleted or SagaState.StatusFailed
            && saga.PaymentTransactionId == result.TransactionId.ToString("D");

        private static string? FindMismatch(
            SagaState saga,
            PaymentResultV1 result)
        {
            if (saga.EscrowId != result.EscrowId)
            {
                return "Payment result escrowId does not match the stored saga.";
            }
            if (!string.Equals(
                saga.BookingId,
                result.BookingId,
                StringComparison.Ordinal))
            {
                return "Payment result bookingId does not match the stored saga.";
            }
            if (!string.Equals(
                saga.Operation,
                result.Operation,
                StringComparison.Ordinal))
            {
                return "Payment result operation does not match the stored saga.";
            }
            if (saga.RequestedAmount != result.Amount)
            {
                return "Payment result amount does not match the stored saga.";
            }

            var expectedCurrency = saga.PaymentRequest?.Currency;
            if (string.IsNullOrWhiteSpace(expectedCurrency)
                || !string.Equals(
                    expectedCurrency,
                    result.Currency,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Payment result currency does not match the stored saga.";
            }
            if (!string.IsNullOrWhiteSpace(saga.PaymentTransactionId)
                && !string.Equals(
                    saga.PaymentTransactionId,
                    result.TransactionId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Payment result transactionId does not match the stored saga.";
            }

            return null;
        }

        private static void ValidateResult(PaymentResultV1 result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.SchemaVersion != PaymentResultV1.CurrentSchemaVersion)
            {
                throw new ArgumentException(
                    $"Unsupported payment result schema version {result.SchemaVersion}.",
                    nameof(result));
            }
            if (result.SagaId == Guid.Empty
                || result.EscrowId == Guid.Empty
                || result.TransactionId == Guid.Empty)
            {
                throw new ArgumentException(
                    "sagaId, escrowId, and transactionId are required.",
                    nameof(result));
            }
            if (string.IsNullOrWhiteSpace(result.BookingId))
            {
                throw new ArgumentException("bookingId is required.", nameof(result));
            }
            if (result.Operation is not (
                PaymentOperation.FundEscrow
                or PaymentOperation.ReleaseEscrow
                or PaymentOperation.RefundEscrow))
            {
                throw new ArgumentException(
                    $"Unsupported payment operation '{result.Operation}'.",
                    nameof(result));
            }
            if (result.Amount <= 0)
            {
                throw new ArgumentException(
                    "Payment result amount must be greater than zero.",
                    nameof(result));
            }
            if (string.IsNullOrWhiteSpace(result.Currency)
                || result.Currency.Trim().Length != 3)
            {
                throw new ArgumentException(
                    "Payment result currency must be a three-letter code.",
                    nameof(result));
            }
            if (result.Status is not (
                PaymentResultV1.StatusApproved
                or PaymentResultV1.StatusDeclined))
            {
                throw new ArgumentException(
                    $"Unsupported payment result status '{result.Status}'.",
                    nameof(result));
            }
        }
    }
}
