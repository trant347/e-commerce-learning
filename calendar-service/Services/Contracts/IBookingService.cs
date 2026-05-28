using calendar_service.Model;

namespace calendar_service.Services.Contracts
{
    public interface IBookingService
    {
        Task<Booking> CreateAsync(
            string taskMasterId,
            string taskMasterUsername,
            string requesterUsername,
            DateTime slotStartUtc,
            int durationHours,
            string? message);

        /// <summary>
        /// Returns slots visible on the TaskMaster's timetable: all ACCEPTED slots, and
        /// PENDING slots if the caller is the TaskMaster, an admin, or the requester.
        /// Past slots are hidden unless the caller is admin or the TaskMaster owner.
        /// </summary>
        Task<List<Booking>> GetTimetableAsync(
            string taskMasterId,
            string? callerUsername,
            bool callerIsAdmin,
            bool callerIsTaskMaster);

        Task<List<Booking>> ListIncomingForTaskMasterAsync(string taskMasterUsername, string? status);
        Task<List<Booking>> ListOutgoingForRequesterAsync(string requesterUsername, string? status);
        Task<Booking?> GetByIdAsync(string id);

        /// <summary>
        /// Marks the booking ACCEPTED and atomically declines every other PENDING booking
        /// for the same (taskMasterId, slotStart). Returns the updated booking plus the list
        /// of auto-declined bookings so the caller can fan out notifications.
        /// </summary>
        Task<AcceptResult> AcceptAsync(string bookingId, string callerUsername, string? responseMessage);

        Task<Booking?> DeclineAsync(string bookingId, string callerUsername, string? responseMessage);

        /// <summary>
        /// Cascade-cleanup after a user is deleted. Removes every booking where the user
        /// is either the requester or the TaskMaster owner. Returns the deleted count.
        /// Idempotent: safe to invoke on Kafka redelivery.
        /// </summary>
        Task<long> DeleteForUserAsync(string username);
    }

    public class AcceptResult
    {
        public Booking Accepted { get; set; } = default!;
        public List<Booking> AutoDeclined { get; set; } = new();
    }
}
