using worker_service.Contracts;

namespace worker_service.Services
{
    public interface IProcessBookingService
    {
        public Task<BookingResult> ProcessBookingAsync(BookingJobMessage bookingMessage);
    }
}
