using calendar_service.Model;
using calendar_service.Services.Contracts;
using calendar_service.Services.DAO;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace calendar_service.Services.Implementation
{
    public class BookingService : IBookingService
    {
        private readonly IMongoCollection<Booking> _collection;
        private readonly ILogger<BookingService> _logger;

        public BookingService(IMongoDBService database, ILogger<BookingService> logger)
        {
            _collection = database.GetCollection<Booking>("Booking");
            _logger = logger;
            EnsureIndexes();
        }

        /// <summary>
        /// Creates indexes on first use. Also installs a partial-unique index that
        /// makes two ACCEPTED bookings sharing the exact same (TaskMasterId, SlotStart)
        /// a duplicate-key error — used as a race guard in <see cref="AcceptAsync"/>.
        /// </summary>
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

        /// <summary>
        /// Creates a new PENDING booking for <paramref name="requesterUsername"/> against
        /// <paramref name="taskMasterId"/> covering the hour-aligned range
        /// [<paramref name="slotStartUtc"/>, slotStartUtc + <paramref name="durationHours"/>).
        /// </summary>
        /// <remarks>
        /// Validates duration (1..<see cref="Booking.MaxDurationHours"/>), rejects past slots,
        /// and rejects self-booking. The booking is rejected if the requested range overlaps
        /// (a) any ACCEPTED booking for the same TaskMaster, or (b) any of this requester's own
        /// PENDING/ACCEPTED bookings for the same TaskMaster.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when validation fails or an overlap is detected.
        /// </exception>
        public async Task<Booking> CreateAsync(
            string taskMasterId,
            string taskMasterUsername,
            string requesterUsername,
            DateTime slotStartUtc,
            int durationHours,
            string? message)
        {
            taskMasterUsername = NormalizeUsername(taskMasterUsername);
            requesterUsername = NormalizeUsername(requesterUsername);

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
            if (taskMasterUsername == requesterUsername)
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
            if (ownOverlap.Any(b => b.RequesterUsername == requesterUsername))
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

        /// <summary>
        /// Returns the bookings to display on a TaskMaster's timetable, filtered for the caller's role.
        /// Admins and the owning TaskMaster see ACCEPTED + PENDING + DECLINED (including past slots);
        /// other callers see ACCEPTED slots plus their own PENDING bookings, and past slots are hidden.
        /// </summary>
        public async Task<List<Booking>> GetTimetableAsync(
            string taskMasterId,
            string? callerUsername,
            bool callerIsAdmin,
            bool callerIsTaskMaster)
        {
            callerUsername = string.IsNullOrEmpty(callerUsername) ? callerUsername : NormalizeUsername(callerUsername);

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

        /// <summary>
        /// Lists incoming bookings addressed to <paramref name="taskMasterUsername"/>, newest first.
        /// Optionally filters by status (PENDING / ACCEPTED / DECLINED / CANCELLED).
        /// </summary>
        public Task<List<Booking>> ListIncomingForTaskMasterAsync(string taskMasterUsername, string? status)
        {
            var fb = Builders<Booking>.Filter;
            var filter = fb.Eq(b => b.TaskMasterUsername, NormalizeUsername(taskMasterUsername));
            if (!string.IsNullOrEmpty(status)) filter &= fb.Eq(b => b.Status, status);
            return _collection.Find(filter).SortByDescending(b => b.CreatedAt).ToListAsync();
        }

        /// <summary>
        /// Lists bookings raised by <paramref name="requesterUsername"/>, newest first.
        /// Optionally filters by status.
        /// </summary>
        public Task<List<Booking>> ListOutgoingForRequesterAsync(string requesterUsername, string? status)
        {
            var fb = Builders<Booking>.Filter;
            var filter = fb.Eq(b => b.RequesterUsername, NormalizeUsername(requesterUsername));
            if (!string.IsNullOrEmpty(status)) filter &= fb.Eq(b => b.Status, status);
            return _collection.Find(filter).SortByDescending(b => b.CreatedAt).ToListAsync();
        }

        /// <summary>Looks up a single booking by its Mongo ObjectId. Returns null if not found.</summary>
        public Task<Booking?> GetByIdAsync(string id)
        {
            return _collection.Find(b => b.Id == id).FirstOrDefaultAsync()!;
        }

        /// <summary>
        /// Accepts a PENDING booking on behalf of the TaskMaster owner and atomically auto-declines
        /// every other PENDING booking whose range overlaps the accepted slot.
        /// </summary>
        /// <returns>The accepted booking plus the list of bookings that were auto-declined.</returns>
        /// <exception cref="KeyNotFoundException">No booking with the given id.</exception>
        /// <exception cref="UnauthorizedAccessException">Caller is not the TaskMaster owner.</exception>
        /// <exception cref="InvalidOperationException">
        /// Booking is not PENDING, or another ACCEPTED booking already overlaps this range.
        /// </exception>
        public async Task<AcceptResult> AcceptAsync(string bookingId, string callerUsername, string? responseMessage)
        {
            callerUsername = NormalizeUsername(callerUsername);

            var existing = await _collection.Find(b => b.Id == bookingId).FirstOrDefaultAsync()
                ?? throw new KeyNotFoundException("Booking not found");

            if (existing.TaskMasterUsername != callerUsername)
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

        /// <summary>
        /// Declines a PENDING booking on behalf of the TaskMaster owner. Returns the updated
        /// booking, or null if no booking with that id exists.
        /// </summary>
        /// <exception cref="UnauthorizedAccessException">Caller is not the TaskMaster owner.</exception>
        /// <exception cref="InvalidOperationException">Booking is not in PENDING state.</exception>
        public async Task<Booking?> DeclineAsync(string bookingId, string callerUsername, string? responseMessage)
        {
            callerUsername = NormalizeUsername(callerUsername);

            var existing = await _collection.Find(b => b.Id == bookingId).FirstOrDefaultAsync();
            if (existing == null) return null;

            if (existing.TaskMasterUsername != callerUsername)
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

        /// <summary>
        /// Cascade-cleanup hook invoked after a user is deleted upstream. Removes every
        /// booking where the user is either requester or TaskMaster owner. Idempotent
        /// (safe on Kafka redelivery) and logs the counts for traceability.
        /// </summary>
        /// <returns>The number of bookings actually deleted.</returns>
        public async Task<long> DeleteForUserAsync(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                _logger.LogWarning("DeleteForUserAsync called with empty username; skipping.");
                return 0;
            }

            var normalized = NormalizeUsername(username);
            var fb = Builders<Booking>.Filter;
            // Plain equality on normalized (lowercase) usernames hits the existing
            // (RequesterUsername, Status) and (TaskMasterUsername, Status) indexes.
            var filter = fb.Eq(b => b.RequesterUsername, normalized)
                         | fb.Eq(b => b.TaskMasterUsername, normalized);

            var matched = await _collection.CountDocumentsAsync(filter);
            _logger.LogInformation(
                "Deleting bookings for user '{Username}' (normalized='{Normalized}'): {Matched} match(es) found.",
                username, normalized, matched);

            var result = await _collection.DeleteManyAsync(filter);
            _logger.LogInformation(
                "Deleted {DeletedCount} booking(s) for user '{Normalized}' (acknowledged={Acknowledged}).",
                result.DeletedCount, normalized, result.IsAcknowledged);
            return result.DeletedCount;
        }

        // Usernames are stored and compared lowercased so that lookups can use plain
        // equality (and therefore the existing indexes) instead of case-insensitive regex.
        private static string NormalizeUsername(string username) =>
            string.IsNullOrEmpty(username) ? username : username.Trim().ToLowerInvariant();

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
