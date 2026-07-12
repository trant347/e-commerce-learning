using calendar_service.Model;
using calendar_service.Services.Contracts;
using calendar_service.Services.DAO;
using MongoDB.Driver;

namespace calendar_service.Services.Implementation
{
    public class SagaStateService : ISagaStateService
    {
        private readonly IMongoCollection<SagaState> _collection;
        private readonly ILogger<SagaStateService> _logger;

        public SagaStateService(IMongoDBService database, ILogger<SagaStateService> logger, IConfiguration configuration)
        {
            _collection = database.GetCollection<SagaState>("SagaState");
            _logger = logger;
            var retentionDays = configuration.GetValue("SagaState:RetentionDays", 90);
            EnsureIndexes(retentionDays);
        }

        /// <summary>
        /// SagaId is the idempotency key shared with payment-service, so it must be unique.
        /// The (Status, CreatedAt) index supports the reconciliation job's sweep for stuck
        /// STARTED rows without a collection scan. The (BookingId, CreatedAt desc) index
        /// supports GetLatestByBookingIdAsync's "is a payment for this booking currently
        /// pending?" check. The TTL index prunes terminal
        /// (COMPLETED/FAILED) rows after <paramref name="retentionDays"/> so the collection
        /// doesn't grow unboundedly forever — STARTED rows are exempt (partial filter) since
        /// they're either actively in-flight or the reconciliation job's responsibility, never
        /// something we want Mongo to silently delete out from under it.
        /// </summary>
        private void EnsureIndexes(int retentionDays)
        {
            var keys = Builders<SagaState>.IndexKeys;

            _collection.Indexes.CreateOne(new CreateIndexModel<SagaState>(
                keys.Ascending(s => s.SagaId),
                new CreateIndexOptions<SagaState> { Name = "uniq_saga_id", Unique = true }));

            _collection.Indexes.CreateOne(new CreateIndexModel<SagaState>(
                keys.Ascending(s => s.Status).Ascending(s => s.CreatedAt),
                new CreateIndexOptions<SagaState> { Name = "status_createdat" }));

            _collection.Indexes.CreateOne(new CreateIndexModel<SagaState>(
                keys.Ascending(s => s.BookingId).Descending(s => s.CreatedAt),
                new CreateIndexOptions<SagaState> { Name = "bookingid_createdat_desc" }));

            try
            {
                _collection.Indexes.CreateOne(new CreateIndexModel<SagaState>(
                    keys.Ascending(s => s.UpdatedAt),
                    new CreateIndexOptions<SagaState>
                    {
                        Name = "ttl_terminal_updatedat",
                        ExpireAfter = TimeSpan.FromDays(retentionDays),
                        PartialFilterExpression = Builders<SagaState>.Filter.In(
                            s => s.Status, new[] { SagaState.StatusCompleted, SagaState.StatusFailed })
                    }));
            }
            catch (MongoCommandException ex)
            {
                // If an index with this name already exists with different options (e.g. a
                // different retention period from a previous deploy), Mongo rejects the create
                // rather than silently changing it. Don't crash startup over a TTL tuning
                // mismatch — log it so an operator can drop/recreate the index manually if the
                // retention window genuinely needs to change.
                _logger.LogWarning(ex, "Could not create/verify SagaState TTL index (ttl_terminal_updatedat); existing index may have different options. Retention cleanup may be using a stale configuration until this is resolved manually.");
            }
        }

        public async Task<SagaState> StartAsync(string bookingId, Guid sagaId, decimal requestedAmount)
        {
            var now = DateTime.UtcNow;
            var saga = new SagaState
            {
                SagaId = sagaId,
                BookingId = bookingId,
                Status = SagaState.StatusStarted,
                RequestedAmount = requestedAmount,
                CreatedAt = now,
                UpdatedAt = now
            };
            await _collection.InsertOneAsync(saga);
            _logger.LogInformation("SagaState STARTED sagaId={SagaId} bookingId={BookingId} amount={Amount}",
                sagaId, bookingId, requestedAmount);
            return saga;
        }

        public async Task<SagaState?> CompleteAsync(Guid sagaId, string paymentTransactionId)
        {
            var update = Builders<SagaState>.Update
                .Set(s => s.Status, SagaState.StatusCompleted)
                .Set(s => s.PaymentTransactionId, paymentTransactionId)
                .Set(s => s.UpdatedAt, DateTime.UtcNow);

            var result = await _collection.FindOneAndUpdateAsync(
                s => s.SagaId == sagaId,
                update,
                new FindOneAndUpdateOptions<SagaState> { ReturnDocument = ReturnDocument.After });

            if (result == null)
            {
                _logger.LogWarning("CompleteAsync: no SagaState found for sagaId={SagaId}", sagaId);
            }
            return result;
        }

        public async Task<SagaState?> FailAsync(Guid sagaId, string failureReason)
        {
            var update = Builders<SagaState>.Update
                .Set(s => s.Status, SagaState.StatusFailed)
                .Set(s => s.FailureReason, failureReason)
                .Set(s => s.UpdatedAt, DateTime.UtcNow);

            var result = await _collection.FindOneAndUpdateAsync(
                s => s.SagaId == sagaId,
                update,
                new FindOneAndUpdateOptions<SagaState> { ReturnDocument = ReturnDocument.After });

            if (result == null)
            {
                _logger.LogWarning("FailAsync: no SagaState found for sagaId={SagaId}", sagaId);
            }
            return result;
        }

        public async Task<SagaState?> GetBySagaIdAsync(Guid sagaId) =>
            await _collection.Find(s => s.SagaId == sagaId).FirstOrDefaultAsync();

        public async Task<SagaState?> GetLatestByBookingIdAsync(string bookingId) =>
            await _collection.Find(s => s.BookingId == bookingId)
                .SortByDescending(s => s.CreatedAt)
                .Limit(1)
                .FirstOrDefaultAsync();

        public Task<List<SagaState>> FindStuckAsync(TimeSpan stuckThreshold)
        {
            var cutoff = DateTime.UtcNow - stuckThreshold;
            return _collection.Find(s => s.Status == SagaState.StatusStarted && s.CreatedAt < cutoff).ToListAsync();
        }

        public async Task<SagaState?> TryClaimAsync(Guid sagaId, TimeSpan claimTtl)
        {
            var now = DateTime.UtcNow;
            var claimCutoff = now - claimTtl;

            // Atomic on a single document: only matches (and claims) this saga if it's still
            // STARTED and no other instance holds a live claim on it. If another replica's
            // FindOneAndUpdate already set ReconciliationClaimedAt to "now" first, this filter
            // won't match and we correctly return null instead of racing it.
            var filter = Builders<SagaState>.Filter.And(
                Builders<SagaState>.Filter.Eq(s => s.SagaId, sagaId),
                Builders<SagaState>.Filter.Eq(s => s.Status, SagaState.StatusStarted),
                Builders<SagaState>.Filter.Or(
                    Builders<SagaState>.Filter.Eq(s => s.ReconciliationClaimedAt, null),
                    Builders<SagaState>.Filter.Lt(s => s.ReconciliationClaimedAt, claimCutoff)));

            var update = Builders<SagaState>.Update.Set(s => s.ReconciliationClaimedAt, now);

            return await _collection.FindOneAndUpdateAsync(
                filter, update, new FindOneAndUpdateOptions<SagaState> { ReturnDocument = ReturnDocument.After });
        }
    }
}
