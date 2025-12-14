using worker_service.Model;

namespace worker_service.DAO
{
    public interface IBookingService
    {
        Task<Booking>  CreateBookingAsync(Booking newBooking);
        Task<Booking?> GetBookingByIdAsync(string id);
        Task<List<Booking>> GetAllBookingsAsync();
        Task UpdateBookingAsync(Booking booking);
    }
}
