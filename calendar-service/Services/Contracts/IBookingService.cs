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
            string? message,
            decimal? offeredRatePerHour = null);

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
        /// TaskMaster owner submits proof of the completed job (a file/image URL) plus the
        /// invoice amount. Moves the booking from ACCEPTED to IMPLEMENTED.
        /// </summary>
        /// <exception cref="KeyNotFoundException">No booking with the given id.</exception>
        /// <exception cref="UnauthorizedAccessException">Caller is not the TaskMaster owner.</exception>
        /// <exception cref="InvalidOperationException">Booking is not ACCEPTED, or invoiceAmount &lt;= 0.</exception>
        Task<Booking> SubmitProofAsync(string bookingId, string callerUsername, string proofFileUrl, decimal invoiceAmount);

        /// <summary>
        /// Requester confirms payment (already processed by payment-service) with the resulting
        /// transaction id. Moves the booking from IMPLEMENTED to COMPLETED.
        /// </summary>
        /// <exception cref="KeyNotFoundException">No booking with the given id.</exception>
        /// <exception cref="UnauthorizedAccessException">Caller is not the requester.</exception>
        /// <exception cref="InvalidOperationException">Booking is not IMPLEMENTED.</exception>
        Task<Booking> CompletePaymentAsync(string bookingId, string callerUsername, string paymentTransactionId);

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
