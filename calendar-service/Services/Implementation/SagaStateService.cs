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

        public SagaStateService(IMongoDBService database, ILogger<SagaStateService> logger)
        {
            _collection = database.GetCollection<SagaState>("SagaState");
            _logger = logger;
            EnsureIndexes();
        }

        /// <summary>
        /// SagaId is the idempotency key shared with payment-service, so it must be unique.
        /// The (Status, CreatedAt) index supports the reconciliation job's sweep for stuck
        /// STARTED rows without a collection scan.
        /// </summary>
        private void EnsureIndexes()
        {
            var keys = Builders<SagaState>.IndexKeys;

            _collection.Indexes.CreateOne(new CreateIndexModel<SagaState>(
                keys.Ascending(s => s.SagaId),
                new CreateIndexOptions<SagaState> { Name = "uniq_saga_id", Unique = true }));

            _collection.Indexes.CreateOne(new CreateIndexModel<SagaState>(
                keys.Ascending(s => s.Status).Ascending(s => s.CreatedAt),
                new CreateIndexOptions<SagaState> { Name = "status_createdat" }));
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

        public Task<List<SagaState>> FindStuckAsync(TimeSpan stuckThreshold)
        {
            var cutoff = DateTime.UtcNow - stuckThreshold;
            return _collection.Find(s => s.Status == SagaState.StatusStarted && s.CreatedAt < cutoff).ToListAsync();
        }
    }
}
