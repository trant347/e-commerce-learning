using calendar_service.Model;
using calendar_service.Services.DAO;
using calendar_service.Services.Implementation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using Xunit;

namespace calendar_service.Tests
{
    /// <summary>
    /// Unit tests for <see cref="BookingService.AcceptAsync"/>, focused on the overlap guard
    /// that prevents a TaskMaster from accepting a PENDING booking whose range overlaps a
    /// slot they're already occupied for (ACCEPTED, IMPLEMENTED or COMPLETED).
    ///
    /// See <see cref="BookingServiceCreateTests"/> for the mocking approach: Moq stands in
    /// for <see cref="IMongoCollection{Booking}"/>, and each call to Find(...).ToListAsync()/
    /// FirstOrDefaultAsync() pulls the next queued result.
    /// </summary>
    public class BookingServiceAcceptTests
    {
        private const string TaskMasterId = "tm-1";
        private const string TaskMasterUsername = "owner";
        private const string RequesterUsername = "alice";

        private static (BookingService svc, Mock<IMongoCollection<Booking>> col)
            BuildService(Queue<List<Booking>> findResultsQueue)
        {
            var col = new Mock<IMongoCollection<Booking>>(MockBehavior.Loose);

            var indexes = new Mock<IMongoIndexManager<Booking>>(MockBehavior.Loose);
            col.SetupGet(c => c.Indexes).Returns(indexes.Object);

            // Every Find(...).FirstOrDefaultAsync()/ToListAsync() pulls the next list off the
            // queue. FirstOrDefaultAsync() just takes the first element (or null) of whatever
            // list we hand back for that call.
            col.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<Booking>>(),
                    It.IsAny<FindOptions<Booking, Booking>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var next = findResultsQueue.Count > 0 ? findResultsQueue.Dequeue() : new List<Booking>();
                    return BuildCursor(next);
                });

            var db = new Mock<IMongoDBService>();
            db.Setup(d => d.GetCollection<Booking>("Booking")).Returns(col.Object);

            var svc = new BookingService(db.Object, NullLogger<BookingService>.Instance);
            return (svc, col);
        }

        private static IAsyncCursor<Booking> BuildCursor(List<Booking> docs)
        {
            var cursor = new Mock<IAsyncCursor<Booking>>();
            cursor.SetupGet(c => c.Current).Returns(docs);
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>())).Returns(true).Returns(false);
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true).ReturnsAsync(false);
            return cursor.Object;
        }

        private static DateTime FutureSlot(int hoursFromNowFloor = 24)
        {
            var t = DateTime.UtcNow.AddHours(hoursFromNowFloor);
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
        }

        private static Booking PendingBookingToAccept(string id, DateTime slot, int durationHours = 2) => new()
        {
            Id = id,
            TaskMasterId = TaskMasterId,
            TaskMasterUsername = TaskMasterUsername,
            RequesterUsername = RequesterUsername,
            SlotStart = slot,
            DurationHours = durationHours,
            Status = Booking.StatusPending,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

        [Fact]
        public async Task AcceptAsync_OverlapsExistingImplementedBooking_Throws()
        {
            var slot = FutureSlot(48);
            var pending = PendingBookingToAccept("pending-1", slot);

            // A different booking for the same TaskMaster, already IMPLEMENTED (job done,
            // invoice pending), overlapping the pending booking's range.
            var existingImplemented = new Booking
            {
                Id = "existing-implemented",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = TaskMasterUsername,
                RequesterUsername = "bob",
                SlotStart = slot,
                DurationHours = 2,
                Status = Booking.StatusImplemented,
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            };

            var queue = new Queue<List<Booking>>();
            queue.Enqueue(new List<Booking> { pending });               // Find-by-id
            queue.Enqueue(new List<Booking> { existingImplemented });   // occupied-slot overlap query
            var (svc, col) = BuildService(queue);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.AcceptAsync(pending.Id!, TaskMasterUsername, responseMessage: null));

            Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
            col.Verify(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<Booking>>(), It.IsAny<UpdateDefinition<Booking>>(),
                    It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task AcceptAsync_OverlapsExistingCompletedBooking_Throws()
        {
            var slot = FutureSlot(48);
            var pending = PendingBookingToAccept("pending-2", slot);

            // A different booking for the same TaskMaster, already COMPLETED (paid, terminal),
            // overlapping the pending booking's range.
            var existingCompleted = new Booking
            {
                Id = "existing-completed",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = TaskMasterUsername,
                RequesterUsername = "bob",
                SlotStart = slot,
                DurationHours = 2,
                Status = Booking.StatusCompleted,
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            };

            var queue = new Queue<List<Booking>>();
            queue.Enqueue(new List<Booking> { pending });              // Find-by-id
            queue.Enqueue(new List<Booking> { existingCompleted });    // occupied-slot overlap query
            var (svc, col) = BuildService(queue);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.AcceptAsync(pending.Id!, TaskMasterUsername, responseMessage: null));

            Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
            col.Verify(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<Booking>>(), It.IsAny<UpdateDefinition<Booking>>(),
                    It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task AcceptAsync_NoOverlap_Succeeds()
        {
            var slot = FutureSlot(48);
            var pending = PendingBookingToAccept("pending-3", slot);

            var queue = new Queue<List<Booking>>();
            queue.Enqueue(new List<Booking> { pending });     // Find-by-id
            queue.Enqueue(new List<Booking>());                // occupied-slot overlap query: none
            queue.Enqueue(new List<Booking>());                // pending-siblings overlap query: none
            queue.Enqueue(new List<Booking> { pending });      // final re-fetch after update
            var (svc, col) = BuildService(queue);

            col.Setup(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<Booking>>(), It.IsAny<UpdateDefinition<Booking>>(),
                    It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((UpdateResult?)null);

            var result = await svc.AcceptAsync(pending.Id!, TaskMasterUsername, responseMessage: "sounds good");

            Assert.NotNull(result.Accepted);
            Assert.Empty(result.AutoDeclined);
            col.Verify(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<Booking>>(), It.IsAny<UpdateDefinition<Booking>>(),
                    It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }
}
