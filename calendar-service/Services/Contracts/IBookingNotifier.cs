using calendar_service.Model;

namespace calendar_service.Services.Contracts
{
    /// <summary>
    /// Builds and publishes the booking lifecycle notification envelopes consumed by
    /// notification-service. Centralising the envelopes keeps the notification type strings,
    /// action types and message wording in one place instead of inlined per controller action,
    /// so a non-HTTP caller (e.g. an MCP tool) produces byte-identical notifications.
    /// </summary>
    public interface IBookingNotifier
    {
        /// <summary>
        /// Tells the TaskMaster owner that a new PENDING request arrived. The recipient is
        /// passed explicitly because the booking entity stores usernames lowercased, whereas
        /// this notification addresses the owner name as product-service reported it.
        /// </summary>
        Task RequestSubmittedAsync(Booking booking, string ownerUsername);

        /// <summary>Tells the requester their booking was accepted and escrow funding is next.</summary>
        Task RequestAcceptedAsync(Booking booking);

        /// <summary>Tells the requester the TaskMaster explicitly declined the request.</summary>
        Task RequestDeclinedAsync(Booking booking);

        /// <summary>
        /// Tells the requester their PENDING request was auto-declined because the TaskMaster
        /// accepted an overlapping booking.
        /// </summary>
        Task RequestAutoDeclinedAsync(Booking booking);

        /// <summary>Tells the TaskMaster owner the requester cancelled an unfunded booking.</summary>
        Task BookingCancelledAsync(Booking booking);
    }
}
