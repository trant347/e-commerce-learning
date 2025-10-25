using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services.Contracts;
using calendar_service.Services.DAO;
using MongoDB.Driver;

namespace calendar_service.Services.Implementation
{
    public class BookingService : IBookingService
    {
        private readonly IMongoCollection<Booking> bookingList;

        public BookingService(IMongoDBService database)
        {
            bookingList = database.GetCollection<Booking>("Booking");
        }

        public async Task<Booking> CreateBookingAsync(Booking booking)
        {
            await bookingList.InsertOneAsync(booking);
            return booking;
        }


        public async Task<List<Booking>> GetAllBookingsAsync()
        {
            return await bookingList.Find(Builders<Booking>.Filter.Empty).ToListAsync();
        }

        public async Task<Booking> GetBookingAsync(string id)
        {
            var filter = Builders<Booking>.Filter.Eq(b => b.Id, id);
            return await bookingList.Find(filter).FirstOrDefaultAsync();
        }
    }
}
