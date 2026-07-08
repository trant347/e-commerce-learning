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
    /// Unit tests for <see cref="BookingService.CreateAsync"/>.
    ///
    /// We mock <see cref="IMongoCollection{Booking}"/> with Moq. Mongo driver's
    /// <c>Find(filter).ToListAsync()</c> chain ultimately calls the collection's
    /// <see cref="IMongoCollection{T}.FindAsync{TProjection}(FilterDefinition{T}, FindOptions{T, TProjection}, System.Threading.CancellationToken)"/>,
    /// so stubbing that method controls what <see cref="BookingService"/> sees as
    /// "existing" bookings without spinning up a real MongoDB.
    /// </summary>
    public class BookingServiceCreateTests
    {
        private static readonly string TaskMasterId = "tm-1";
        private const string TaskMasterUsername = "owner";
        private const string RequesterUsername = "alice";

        // ---- helpers ----

        private static (BookingService svc, Mock<IMongoCollection<Booking>> col, List<Booking> inserted)
            BuildService(Queue<List<Booking>> findResultsQueue)
        {
            var col = new Mock<IMongoCollection<Booking>>(MockBehavior.Loose);

            // Indexes.CreateOne is called in the constructor; just hand back a loose mock.
            var indexes = new Mock<IMongoIndexManager<Booking>>(MockBehavior.Loose);
            col.SetupGet(c => c.Indexes).Returns(indexes.Object);

            // Every Find(...).ToListAsync() pulls the next list off the queue.
            col.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<Booking>>(),
                    It.IsAny<FindOptions<Booking, Booking>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var next = findResultsQueue.Count > 0 ? findResultsQueue.Dequeue() : new List<Booking>();
                    return BuildCursor(next);
                });

            var inserted = new List<Booking>();
            col.Setup(c => c.InsertOneAsync(
                    It.IsAny<Booking>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()))
                .Callback<Booking, InsertOneOptions, CancellationToken>((b, _, _) =>
                {
                    b.Id ??= Guid.NewGuid().ToString("N");
                    inserted.Add(b);
                })
                .Returns(Task.CompletedTask);

            var db = new Mock<IMongoDBService>();
            db.Setup(d => d.GetCollection<Booking>("Booking")).Returns(col.Object);

            var svc = new BookingService(db.Object, NullLogger<BookingService>.Instance);
            return (svc, col, inserted);
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
            // Hour-aligned slot at least N hours in the future.
            var t = DateTime.UtcNow.AddHours(hoursFromNowFloor);
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
        }

        // ---- tests ----

        [Fact]
        public async Task CreateAsync_NoConflicts_InsertsPendingBooking()
        {
            // Both overlap queries return empty → booking succeeds.
            var queue = new Queue<List<Booking>>();
            queue.Enqueue(new List<Booking>()); // accepted overlap
            queue.Enqueue(new List<Booking>()); // own pending/accepted overlap
            var (svc, col, inserted) = BuildService(queue);

            var slot = FutureSlot(48);
            var booking = await svc.CreateAsync(
                TaskMasterId, TaskMasterUsername, RequesterUsername,
                slot, durationHours: 3, message: "hello");

            Assert.NotNull(booking);
            Assert.Equal(Booking.StatusPending, booking.Status);
            Assert.Equal(TaskMasterId, booking.TaskMasterId);
            Assert.Equal(RequesterUsername, booking.RequesterUsername);
            Assert.Equal(slot, booking.SlotStart);
            Assert.Equal(3, booking.DurationHours);
            Assert.Equal(slot.AddHours(3), booking.SlotEnd);
            Assert.Equal("hello", booking.RequestMessage);

            Assert.Single(inserted);
            col.Verify(c => c.InsertOneAsync(
                It.IsAny<Booking>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task CreateAsync_OverlapsExistingAcceptedBooking_Throws()
        {
            var slot = FutureSlot(48);
            // An ACCEPTED booking already covers slot..slot+2h. The new request asks for
            // slot+1h..slot+3h → partial overlap, must be rejected.
            var existingAccepted = new Booking
            {
                Id = "existing-1",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = TaskMasterUsername,
                RequesterUsername = "bob",
                SlotStart = slot,
                DurationHours = 2,
                Status = Booking.StatusAccepted,
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            };

            var queue = new Queue<List<Booking>>();
            queue.Enqueue(new List<Booking> { existingAccepted }); // accepted overlap query hits
            var (svc, col, inserted) = BuildService(queue);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreateAsync(
                    TaskMasterId, TaskMasterUsername, RequesterUsername,
                    slot.AddHours(1), durationHours: 2, message: null));

            Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(inserted);
            col.Verify(c => c.InsertOneAsync(
                It.IsAny<Booking>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateAsync_OverlapsExistingImplementedBooking_Throws()
        {
            var slot = FutureSlot(48);
            // An IMPLEMENTED booking (job done, invoice pending) still occupies the slot —
            // the TaskMaster can't be double-booked for time they already worked.
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
            queue.Enqueue(new List<Booking> { existingImplemented }); // occupied-slot overlap query hits
            var (svc, col, inserted) = BuildService(queue);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreateAsync(
                    TaskMasterId, TaskMasterUsername, RequesterUsername,
                    slot.AddHours(1), durationHours: 2, message: null));

            Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(inserted);
            col.Verify(c => c.InsertOneAsync(
                It.IsAny<Booking>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateAsync_OverlapsExistingCompletedBooking_Throws()
        {
            var slot = FutureSlot(48);
            // A COMPLETED booking (paid, terminal state) still represents time the TaskMaster
            // was actually busy — must still block new requests for the same range.
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
            queue.Enqueue(new List<Booking> { existingCompleted }); // occupied-slot overlap query hits
            var (svc, col, inserted) = BuildService(queue);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreateAsync(
                    TaskMasterId, TaskMasterUsername, RequesterUsername,
                    slot.AddHours(1), durationHours: 2, message: null));

            Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(inserted);
            col.Verify(c => c.InsertOneAsync(
                It.IsAny<Booking>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateAsync_RejectsBookingYourself()
        {
            var (svc, _, inserted) = BuildService(new Queue<List<Booking>>());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreateAsync(
                    TaskMasterId,
                    taskMasterUsername: "Owner",       // mixed case on purpose
                    requesterUsername: "owner",
                    FutureSlot(24), durationHours: 1, message: null));

            Assert.Empty(inserted);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(Booking.MaxDurationHours + 1)]
        public async Task CreateAsync_RejectsOutOfRangeDuration(int badHours)
        {
            var (svc, _, inserted) = BuildService(new Queue<List<Booking>>());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreateAsync(
                    TaskMasterId, TaskMasterUsername, RequesterUsername,
                    FutureSlot(48), durationHours: badHours, message: null));

            Assert.Empty(inserted);
        }

        [Fact]
        public async Task CreateAsync_RejectsPastSlot()
        {
            var (svc, _, inserted) = BuildService(new Queue<List<Booking>>());

            var pastSlot = new DateTime(DateTime.UtcNow.Year - 1, 1, 1, 9, 0, 0, DateTimeKind.Utc);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreateAsync(
                    TaskMasterId, TaskMasterUsername, RequesterUsername,
                    pastSlot, durationHours: 1, message: null));

            Assert.Empty(inserted);
        }

        /// <summary>
        /// Race-loser path: two concurrent POSTs both pass the in-memory overlap check
        /// (each runs FindAsync before the other commits its insert), so the second insert
        /// is what trips the unique partial index in MongoDB. The service must translate
        /// the resulting MongoWriteException into the same InvalidOperationException the
        /// controller already maps to HTTP 409 — otherwise the user sees an opaque 500.
        /// </summary>
        [Fact]
        public async Task CreateAsync_DuplicateKeyFromUniqueIndex_ThrowsInvalidOperation()
        {
            // Both overlap queries return empty → in-memory check passes (this is the race window).
            var queue = new Queue<List<Booking>>();
            queue.Enqueue(new List<Booking>());
            queue.Enqueue(new List<Booking>());

            var col = new Mock<IMongoCollection<Booking>>(MockBehavior.Loose);
            var indexes = new Mock<IMongoIndexManager<Booking>>(MockBehavior.Loose);
            col.SetupGet(c => c.Indexes).Returns(indexes.Object);
            col.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<Booking>>(),
                    It.IsAny<FindOptions<Booking, Booking>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => BuildCursor(queue.Count > 0 ? queue.Dequeue() : new List<Booking>()));

            // Simulate the partial-unique index rejecting the insert. MongoWriteException's
            // public constructors vary across driver versions, so we build the instance via
            // FormatterServices.GetUninitializedObject and fill in just the field the
            // production catch clause reads: WriteError.Category == DuplicateKey.
            var duplicateKey = BuildDuplicateKeyMongoWriteException();
            col.Setup(c => c.InsertOneAsync(
                    It.IsAny<Booking>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(duplicateKey);

            var db = new Mock<IMongoDBService>();
            db.Setup(d => d.GetCollection<Booking>("Booking")).Returns(col.Object);
            var svc = new BookingService(db.Object, NullLogger<BookingService>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.CreateAsync(
                    TaskMasterId, TaskMasterUsername, RequesterUsername,
                    FutureSlot(48), durationHours: 1, message: null));

            Assert.Contains("pending or accepted", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds a <see cref="MongoWriteException"/> whose <c>WriteError.Category</c> is
        /// <see cref="ServerErrorCategory.DuplicateKey"/>. We use reflection so the test does
        /// not depend on which constructors the MongoDB driver happens to expose publicly
        /// in a given version.
        /// </summary>
        private static MongoWriteException BuildDuplicateKeyMongoWriteException()
        {
            var writeError = (WriteError)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(WriteError));
            SetBackingField(writeError, "_category", ServerErrorCategory.DuplicateKey);
            SetBackingField(writeError, "_code", 11000);
            SetBackingField(writeError, "_message", "E11000 duplicate key error");

            var ex = (MongoWriteException)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(MongoWriteException));
            SetBackingField(ex, "_writeError", writeError);
            return ex;
        }

        private static void SetBackingField(object target, string fieldName, object? value)
        {
            var type = target.GetType();
            while (type != null)
            {
                var field = type.GetField(fieldName,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }
                type = type.BaseType;
            }
            throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().FullName}");
        }
    }
}
