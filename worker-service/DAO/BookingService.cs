using MongoDB.Driver;
using worker_service.Model;

namespace worker_service.DAO
{
    public class BookingService : IBookingService
    {
        private readonly IMongoCollection<Booking> _bookingsCollection;

        public BookingService(IMongoDatabase database)
        {
            _bookingsCollection = database.GetCollection<Booking>("Booking");
        }

        public async Task<Booking> CreateBookingAsync(Booking newBooking)
        {
            await _bookingsCollection.InsertOneAsync(newBooking);
            return newBooking;
        }

        public async Task<Booking?> GetBookingByIdAsync(string id)
        {
            return await _bookingsCollection.Find(b => b.Id == id).FirstOrDefaultAsync();
        }

        public async Task<List<Booking>> GetAllBookingsAsync()
        {
            return await _bookingsCollection.Find(_ => true).ToListAsync();
        }

        public async Task UpdateBookingAsync(Booking booking)
        {
            var replaceResult = await _bookingsCollection.ReplaceOneAsync(b => b.Id == booking.Id, booking);
            if (replaceResult.MatchedCount == 0)
            {
                throw new Exception($"Booking with ID {booking.Id} not found.");
            }
        }
    }
}
