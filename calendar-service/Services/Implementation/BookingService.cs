using calendar_service.Model;
using calendar_service.Services.Contracts;
using calendar_service.Services.DAO;
using MongoDB.Bson;
using MongoDB.Driver;

namespace calendar_service.Services.Implementation
{
    public class BookingService : IBookingService
    {
        private readonly IMongoCollection<Booking> _collection;

        public BookingService(IMongoDBService database)
        {
            _collection = database.GetCollection<Booking>("Booking");
            EnsureIndexes();
        }

        private void EnsureIndexes()
        {
            var keys = Builders<Booking>.IndexKeys;

            // Weak guard: prevent two ACCEPTED bookings sharing the exact same SlotStart.
            // Full overlap protection is enforced at the application layer in AcceptAsync.
            var acceptedFilter = new BsonDocument("Status", Booking.StatusAccepted);
            _collection.Indexes.CreateOne(new CreateIndexModel<Booking>(
                keys.Ascending(b => b.TaskMasterId).Ascending(b => b.SlotStart),
                new CreateIndexOptions<Booking>
                {
                    Name = "uniq_taskmaster_slot_accepted",
                    Unique = true,
                    PartialFilterExpression = acceptedFilter
                }));                

            _collection.Indexes.CreateOne(new CreateIndexModel<Booking>(
                keys.Ascending(b => b.TaskMasterUsername).Ascending(b => b.Status)));
            _collection.Indexes.CreateOne(new CreateIndexModel<Booking>(
                keys.Ascending(b => b.RequesterUsername).Ascending(b => b.Status)));
            _collection.Indexes.CreateOne(new CreateIndexModel<Booking>(
                keys.Ascending(b => b.TaskMasterId).Ascending(b => b.SlotStart)));
        }

        public async Task<Booking> CreateAsync(
            string taskMasterId,
            string taskMasterUsername,
            string requesterUsername,
            DateTime slotStartUtc,
            int durationHours,
            string? message)
        {
            slotStartUtc = NormalizeToHour(slotStartUtc);
            if (durationHours < 1 || durationHours > Booking.MaxDurationHours)
            {
                throw new InvalidOperationException(
                    $"Duration must be between 1 and {Booking.MaxDurationHours} hours");
            }
            if (slotStartUtc <= DateTime.UtcNow)
            {
                throw new InvalidOperationException("Slot must be in the future");
            }
            if (string.Equals(taskMasterUsername, requesterUsername, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("You cannot book yourself");
            }

            var newEnd = slotStartUtc.AddHours(durationHours);

            // Reject if the requested range overlaps any ACCEPTED booking.
            var acceptedOverlap = await FindOverlappingAsync(
                taskMasterId, slotStartUtc, newEnd, Booking.StatusAccepted);
            if (acceptedOverlap.Any())
            {
                throw new InvalidOperationException("This range overlaps an already-booked slot");
            }

            // Reject if this requester already has a PENDING/ACCEPTED booking overlapping the same range.
            var ownOverlap = await FindOverlappingAsync(
                taskMasterId, slotStartUtc, newEnd, Booking.StatusPending, Booking.StatusAccepted);
            if (ownOverlap.Any(b => string.Equals(b.RequesterUsername, requesterUsername, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("You already have a pending or accepted booking overlapping this range");
            }

            var entity = new Booking
            {
                TaskMasterId = taskMasterId,
                TaskMasterUsername = taskMasterUsername,
                RequesterUsername = requesterUsername,
                SlotStart = slotStartUtc,
                DurationHours = durationHours,
                Status = Booking.StatusPending,
                RequestMessage = message,
                CreatedAt = DateTime.UtcNow
            };
            await _collection.InsertOneAsync(entity);
            return entity;
        }

        public async Task<List<Booking>> GetTimetableAsync(
            string taskMasterId,
            string? callerUsername,
            bool callerIsAdmin,
            bool callerIsTaskMaster)
        {
            var fb = Builders<Booking>.Filter;
            var filter = fb.Eq(b => b.TaskMasterId, taskMasterId);

            // Hide fully-past bookings for everyone except admin and the TaskMaster owner.
            // A booking is past if SlotStart + DurationHours <= now. We can't express that
            // server-side without $expr; bound it with SlotStart >= now - MaxDuration and
            // post-filter in memory.
            if (!callerIsAdmin && !callerIsTaskMaster)
            {
                var nowHour = NormalizeToHour(DateTime.UtcNow);
                filter &= fb.Gte(b => b.SlotStart, nowHour.AddHours(-Booking.MaxDurationHours));
            }

            if (callerIsAdmin || callerIsTaskMaster)
            {
                filter &= fb.In(b => b.Status, new[]
                {
                    Booking.StatusAccepted,
                    Booking.StatusPending,
                    Booking.StatusDeclined
                });
            }
            else
            {
                // Other users only see ACCEPTED (busy) slots and their own PENDING bookings.
                var statusFilter = fb.Eq(b => b.Status, Booking.StatusAccepted);
                if (!string.IsNullOrEmpty(callerUsername))
                {
                    statusFilter |= (fb.Eq(b => b.Status, Booking.StatusPending) &
                                     fb.Eq(b => b.RequesterUsername, callerUsername));
                }
                filter &= statusFilter;
            }

            var list = await _collection.Find(filter)
                .SortBy(b => b.SlotStart)
                .ToListAsync();

            if (!callerIsAdmin && !callerIsTaskMaster)
            {
                var now = DateTime.UtcNow;
                list = list.Where(b => b.SlotEnd > now).ToList();
            }
            return list;
        }

        public Task<List<Booking>> ListIncomingForTaskMasterAsync(string taskMasterUsername, string? status)
        {
            var fb = Builders<Booking>.Filter;
            var filter = fb.Eq(b => b.TaskMasterUsername, taskMasterUsername);
            if (!string.IsNullOrEmpty(status)) filter &= fb.Eq(b => b.Status, status);
            return _collection.Find(filter).SortByDescending(b => b.CreatedAt).ToListAsync();
        }

        public Task<List<Booking>> ListOutgoingForRequesterAsync(string requesterUsername, string? status)
        {
            var fb = Builders<Booking>.Filter;
            var filter = fb.Eq(b => b.RequesterUsername, requesterUsername);
            if (!string.IsNullOrEmpty(status)) filter &= fb.Eq(b => b.Status, status);
            return _collection.Find(filter).SortByDescending(b => b.CreatedAt).ToListAsync();
        }

        public Task<Booking?> GetByIdAsync(string id)
        {
            return _collection.Find(b => b.Id == id).FirstOrDefaultAsync()!;
        }

        public async Task<AcceptResult> AcceptAsync(string bookingId, string callerUsername, string? responseMessage)
        {
            var existing = await _collection.Find(b => b.Id == bookingId).FirstOrDefaultAsync()
                ?? throw new KeyNotFoundException("Booking not found");

            if (!string.Equals(existing.TaskMasterUsername, callerUsername, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Only the TaskMaster can accept this booking");
            }
            if (existing.Status != Booking.StatusPending)
            {
                throw new InvalidOperationException($"Booking is {existing.Status} and cannot be accepted");
            }

            // Reject if any ACCEPTED booking already overlaps this range.
            var acceptedOverlap = await FindOverlappingAsync(
                existing.TaskMasterId, existing.SlotStart, existing.SlotEnd, Booking.StatusAccepted);
            if (acceptedOverlap.Any(b => b.Id != bookingId))
            {
                throw new InvalidOperationException("This range overlaps an already-accepted booking");
            }

            var now = DateTime.UtcNow;

            var update = Builders<Booking>.Update
                .Set(b => b.Status, Booking.StatusAccepted)
                .Set(b => b.ResponseMessage, responseMessage)
                .Set(b => b.RespondedAt, now);
            try
            {
                await _collection.UpdateOneAsync(
                    b => b.Id == bookingId && b.Status == Booking.StatusPending, update);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new InvalidOperationException("This slot was just accepted for another requester");
            }

            // Fan-out: auto-decline every other PENDING booking whose range overlaps the accepted one.
            var pendingOverlap = await FindOverlappingAsync(
                existing.TaskMasterId, existing.SlotStart, existing.SlotEnd, Booking.StatusPending);
            var siblings = pendingOverlap.Where(b => b.Id != bookingId).ToList();

            if (siblings.Count > 0)
            {
                var siblingIds = siblings.Select(s => s.Id).ToList();
                await _collection.UpdateManyAsync(
                    Builders<Booking>.Filter.In(b => b.Id, siblingIds)
                        & Builders<Booking>.Filter.Eq(b => b.Status, Booking.StatusPending),
                    Builders<Booking>.Update
                        .Set(b => b.Status, Booking.StatusDeclined)
                        .Set(b => b.ResponseMessage, "Auto-declined: slot taken by another requester")
                        .Set(b => b.RespondedAt, now));
                foreach (var s in siblings)
                {
                    s.Status = Booking.StatusDeclined;
                    s.RespondedAt = now;
                }
            }

            var accepted = await _collection.Find(b => b.Id == bookingId).FirstAsync();
            return new AcceptResult { Accepted = accepted, AutoDeclined = siblings };
        }

        public async Task<Booking?> DeclineAsync(string bookingId, string callerUsername, string? responseMessage)
        {
            var existing = await _collection.Find(b => b.Id == bookingId).FirstOrDefaultAsync();
            if (existing == null) return null;

            if (!string.Equals(existing.TaskMasterUsername, callerUsername, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Only the TaskMaster can decline this booking");
            }
            if (existing.Status != Booking.StatusPending)
            {
                throw new InvalidOperationException($"Booking is {existing.Status} and cannot be declined");
            }

            var update = Builders<Booking>.Update
                .Set(b => b.Status, Booking.StatusDeclined)
                .Set(b => b.ResponseMessage, responseMessage)
                .Set(b => b.RespondedAt, DateTime.UtcNow);
            await _collection.UpdateOneAsync(b => b.Id == bookingId, update);
            return await _collection.Find(b => b.Id == bookingId).FirstAsync();
        }

        private static DateTime NormalizeToHour(DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
        }

        /// <summary>
        /// Returns bookings for the given task master whose [SlotStart, SlotStart+DurationHours)
        /// range overlaps [rangeStart, rangeEnd) and whose Status is one of <paramref name="statuses"/>.
        /// Uses a bounded server-side prefilter then exact in-memory overlap check.
        /// </summary>
        private async Task<List<Booking>> FindOverlappingAsync(
            string taskMasterId, DateTime rangeStart, DateTime rangeEnd, params string[] statuses)
        {
            var fb = Builders<Booking>.Filter;
            // Bound: a booking can overlap only if it starts strictly before rangeEnd and
            // not earlier than rangeStart - MaxDurationHours.
            var filter = fb.Eq(b => b.TaskMasterId, taskMasterId)
                         & fb.In(b => b.Status, statuses)
                         & fb.Lt(b => b.SlotStart, rangeEnd)
                         & fb.Gte(b => b.SlotStart, rangeStart.AddHours(-Booking.MaxDurationHours));
            var candidates = await _collection.Find(filter).ToListAsync();
            return candidates.Where(b => b.SlotEnd > rangeStart).ToList();
        }
    }
}
