using calendar_service.Model;

namespace calendar_service.Services.Contracts
{
    public interface IBookingService
    {
        Task<Booking> CreateBookingAsync(Booking booking);
        Task<Booking> GetBookingAsync(string id);

        Task<List<Booking>> GetAllBookingsAsync();
    }
}
