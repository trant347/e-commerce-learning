using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services.Contracts;

namespace calendar_service.Services.Implementation
{
    /// <summary>
    /// Maps bookings to the notification-service payload shape
    /// (<c>{ type, recipientUsername, message, actionType, actionPayload }</c>) and publishes
    /// them through <see cref="INotificationProducer"/>.
    /// </summary>
    public sealed class BookingNotifier : IBookingNotifier
    {
        private const string TypeRequestSubmitted = "BOOKING_REQUEST_SUBMITTED";
        private const string TypeRequestAccepted = "BOOKING_REQUEST_ACCEPTED";
        private const string TypeRequestDeclined = "BOOKING_REQUEST_DECLINED";
        private const string TypeBookingCancelled = "BOOKING_CANCELLED";

        private const string ActionViewIncoming = "VIEW_INCOMING_BOOKING_REQUEST";
        private const string ActionViewOutgoing = "VIEW_OUTGOING_BOOKING_REQUEST";
        private const string ActionViewPaymentRequest = "VIEW_PAYMENT_REQUEST";

        private readonly INotificationProducer _notifications;

        public BookingNotifier(INotificationProducer notifications)
        {
            _notifications = notifications;
        }

        public Task RequestSubmittedAsync(Booking booking, string ownerUsername) => PublishAsync(
            TypeRequestSubmitted,
            ownerUsername,
            $"{booking.RequesterUsername} requested to book you from {booking.SlotStart:yyyy-MM-dd HH:mm} to {booking.SlotEnd:HH:mm} UTC.",
            ActionViewIncoming,
            booking);

        public Task RequestAcceptedAsync(Booking booking) => PublishAsync(
            TypeRequestAccepted,
            booking.RequesterUsername,
            $"Your booking from {booking.SlotStart:yyyy-MM-dd HH:mm} to {booking.SlotEnd:HH:mm} UTC was accepted. Fund escrow to confirm the work.",
            ActionViewPaymentRequest,
            booking);

        public Task RequestDeclinedAsync(Booking booking) => PublishAsync(
            TypeRequestDeclined,
            booking.RequesterUsername,
            $"Your booking from {booking.SlotStart:yyyy-MM-dd HH:mm} to {booking.SlotEnd:HH:mm} UTC was declined.",
            ActionViewOutgoing,
            booking);

        public Task RequestAutoDeclinedAsync(Booking booking) => PublishAsync(
            TypeRequestDeclined,
            booking.RequesterUsername,
            $"Your booking from {booking.SlotStart:yyyy-MM-dd HH:mm} to {booking.SlotEnd:HH:mm} UTC was auto-declined (slot taken).",
            ActionViewOutgoing,
            booking);

        public Task BookingCancelledAsync(Booking booking) => PublishAsync(
            TypeBookingCancelled,
            booking.TaskMasterUsername,
            $"{booking.RequesterUsername} cancelled the booking.",
            ActionViewIncoming,
            booking);

        private Task PublishAsync(
            string type,
            string recipientUsername,
            string message,
            string actionType,
            Booking booking) =>
            _notifications.PublishAsync(new
            {
                type,
                recipientUsername,
                message,
                actionType,
                actionPayload = new Dictionary<string, string>
                {
                    { "bookingId", booking.Id ?? string.Empty },
                    { "taskMasterId", booking.TaskMasterId }
                }
            });
    }
}
