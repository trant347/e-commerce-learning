using calendar_service.Model;
using calendar_service.Services;
using calendar_service.Services.Contracts;
using calendar_service.Services.DAO;
using MongoDB.Bson;
using MongoDB.Driver;
using Payment.Contracts.V1;

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

            var activeOperationFilter = new BsonDocument
            {
                { "Status", SagaState.StatusStarted },
                {
                    "Operation",
                    new BsonDocument("$in", new BsonArray
                    {
                        PaymentOperation.FundEscrow,
                        PaymentOperation.ReleaseEscrow,
                        PaymentOperation.RefundEscrow
                    })
                }
            };
            _collection.Indexes.CreateOne(new CreateIndexModel<SagaState>(
                keys.Ascending(s => s.BookingId).Ascending(s => s.Operation),
                new CreateIndexOptions<SagaState>
                {
                    Name = "uniq_active_booking_operation",
                    Unique = true,
                    PartialFilterExpression = activeOperationFilter
                }));

            _collection.Indexes.CreateOne(new CreateIndexModel<SagaState>(
                keys.Ascending(s => s.DispatchStatus).Ascending(s => s.NextDispatchAttemptAt),
                new CreateIndexOptions<SagaState> { Name = "dispatch_status_next_attempt" }));

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

        public async Task<SagaState> EnqueueAsync(
            PaymentRequestedV1 request,
            string? traceParent = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);

            var normalizedRequest = request with
            {
                BookingId = request.BookingId.Trim(),
                Currency = request.Currency.Trim().ToUpperInvariant(),
                PayerUserId = request.PayerUserId.Trim(),
                PayeeUserId = request.PayeeUserId.Trim(),
                TaskMasterUserId = request.TaskMasterUserId.Trim(),
                PaymentMethodToken = string.IsNullOrWhiteSpace(request.PaymentMethodToken)
                    ? null
                    : request.PaymentMethodToken.Trim()
            };
            var now = DateTime.UtcNow;
            var saga = new SagaState
            {
                SagaId = normalizedRequest.SagaId,
                BookingId = normalizedRequest.BookingId,
                EscrowId = normalizedRequest.EscrowId,
                Operation = normalizedRequest.Operation,
                Status = SagaState.StatusStarted,
                RequestedAmount = normalizedRequest.Amount,
                PaymentRequest = PendingPaymentRequest.FromContract(normalizedRequest),
                DispatchStatus = SagaDispatchStatus.PENDING,
                DispatchAttemptCount = 0,
                NextDispatchAttemptAt = now,
                TraceParent = string.IsNullOrWhiteSpace(traceParent) ? null : traceParent.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            };

            try
            {
                await _collection.InsertOneAsync(
                    saga,
                    cancellationToken: cancellationToken);
            }
            catch (MongoWriteException ex) when (IsActiveOperationDuplicate(ex))
            {
                throw new ActivePaymentSagaException(saga.BookingId, saga.Operation!);
            }
            catch (MongoWriteException ex) when (
                ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                throw;
            }
            catch (MongoException ex)
            {
                throw new SagaOutboxPersistenceException(
                    "Payment saga and outbox request could not be persisted.",
                    ex);
            }
            catch (TimeoutException ex)
            {
                throw new SagaOutboxPersistenceException(
                    "Payment saga and outbox request could not be persisted.",
                    ex);
            }

            _logger.LogInformation(
                "Enqueued payment saga sagaId={SagaId} bookingId={BookingId} escrowId={EscrowId} operation={Operation}",
                saga.SagaId,
                saga.BookingId,
                saga.EscrowId,
                saga.Operation);
            return saga;
        }

        public async Task<SagaState?> TryClaimNextDispatchAsync(
            TimeSpan claimLease,
            CancellationToken cancellationToken = default)
        {
            if (claimLease <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(claimLease),
                    "Dispatch claim lease must be greater than zero.");
            }

            var now = DateTime.UtcNow;
            var filter = Builders<SagaState>.Filter.And(
                Builders<SagaState>.Filter.Eq(s => s.Status, SagaState.StatusStarted),
                Builders<SagaState>.Filter.Ne(s => s.PaymentRequest, null),
                Builders<SagaState>.Filter.Or(
                    Builders<SagaState>.Filter.And(
                        Builders<SagaState>.Filter.Eq(
                            s => s.DispatchStatus,
                            SagaDispatchStatus.PENDING),
                        Builders<SagaState>.Filter.Lte(
                            s => s.NextDispatchAttemptAt,
                            now)),
                    Builders<SagaState>.Filter.And(
                        Builders<SagaState>.Filter.Eq(
                            s => s.DispatchStatus,
                            SagaDispatchStatus.CLAIMED),
                        Builders<SagaState>.Filter.Lte(
                            s => s.DispatchClaimExpiresAt,
                            now))));

            var update = Builders<SagaState>.Update
                .Set(s => s.DispatchStatus, SagaDispatchStatus.CLAIMED)
                .Set(s => s.DispatchClaimedAt, now)
                .Set(s => s.DispatchClaimExpiresAt, now.Add(claimLease))
                .Set(s => s.LastDispatchAttemptAt, now)
                .Inc(s => s.DispatchAttemptCount, 1)
                .Set(s => s.UpdatedAt, now);

            return await _collection.FindOneAndUpdateAsync(
                filter,
                update,
                new FindOneAndUpdateOptions<SagaState>
                {
                    ReturnDocument = ReturnDocument.After,
                    Sort = Builders<SagaState>.Sort
                        .Ascending(s => s.NextDispatchAttemptAt)
                        .Ascending(s => s.CreatedAt)
                },
                cancellationToken);
        }

        public Task<long> GetDispatchBacklogAsync(
            CancellationToken cancellationToken = default) =>
            _collection.CountDocumentsAsync(
                Builders<SagaState>.Filter.And(
                    Builders<SagaState>.Filter.Eq(
                        saga => saga.Status,
                        SagaState.StatusStarted),
                    Builders<SagaState>.Filter.Ne(
                        saga => saga.PaymentRequest,
                        null),
                    Builders<SagaState>.Filter.Ne(
                        saga => saga.DispatchStatus,
                        SagaDispatchStatus.DISPATCHED)),
                cancellationToken: cancellationToken);

        public async Task<bool> MarkDispatchedAsync(
            Guid sagaId,
            DateTime claimTimestamp,
            CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var filter = CurrentDispatchClaimFilter(sagaId, claimTimestamp);
            var update = Builders<SagaState>.Update
                .Set(s => s.DispatchStatus, SagaDispatchStatus.DISPATCHED)
                .Set(s => s.DispatchedAt, now)
                .Set(s => s.DispatchClaimedAt, null)
                .Set(s => s.DispatchClaimExpiresAt, null)
                .Set(s => s.NextDispatchAttemptAt, null)
                .Set(s => s.LastDispatchError, null)
                .Set(s => s.UpdatedAt, now);

            var result = await _collection.UpdateOneAsync(
                filter,
                update,
                cancellationToken: cancellationToken);
            return result.ModifiedCount == 1;
        }

        public async Task<bool> RescheduleDispatchAsync(
            Guid sagaId,
            DateTime claimTimestamp,
            DateTime nextAttemptAt,
            string error,
            CancellationToken cancellationToken = default)
        {
            if (nextAttemptAt.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Next dispatch attempt must be expressed in UTC.",
                    nameof(nextAttemptAt));
            }
            if (string.IsNullOrWhiteSpace(error))
            {
                throw new ArgumentException(
                    "Dispatch failure reason is required.",
                    nameof(error));
            }

            var now = DateTime.UtcNow;
            var filter = CurrentDispatchClaimFilter(sagaId, claimTimestamp);
            var update = Builders<SagaState>.Update
                .Set(s => s.DispatchStatus, SagaDispatchStatus.PENDING)
                .Set(s => s.NextDispatchAttemptAt, nextAttemptAt)
                .Set(s => s.LastDispatchError, Truncate(error.Trim(), 2000))
                .Set(s => s.DispatchClaimedAt, null)
                .Set(s => s.DispatchClaimExpiresAt, null)
                .Set(s => s.UpdatedAt, now);

            var result = await _collection.UpdateOneAsync(
                filter,
                update,
                cancellationToken: cancellationToken);
            return result.ModifiedCount == 1;
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

        public async Task<bool> CompleteResultAsync(
            Guid sagaId,
            string paymentTransactionId,
            CancellationToken cancellationToken = default)
        {
            var result = await _collection.UpdateOneAsync(
                s => s.SagaId == sagaId && s.Status == SagaState.StatusStarted,
                Builders<SagaState>.Update
                    .Set(s => s.Status, SagaState.StatusCompleted)
                    .Set(s => s.PaymentTransactionId, paymentTransactionId)
                    .Set(s => s.FailureReason, null)
                    .Set(s => s.UpdatedAt, DateTime.UtcNow),
                cancellationToken: cancellationToken);
            return result.ModifiedCount == 1;
        }

        public async Task<bool> FailResultAsync(
            Guid sagaId,
            string paymentTransactionId,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            var result = await _collection.UpdateOneAsync(
                s => s.SagaId == sagaId && s.Status == SagaState.StatusStarted,
                Builders<SagaState>.Update
                    .Set(s => s.Status, SagaState.StatusFailed)
                    .Set(s => s.PaymentTransactionId, paymentTransactionId)
                    .Set(s => s.FailureReason, Truncate(failureReason.Trim(), 2000))
                    .Set(s => s.UpdatedAt, DateTime.UtcNow),
                cancellationToken: cancellationToken);
            return result.ModifiedCount == 1;
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
            return _collection.Find(
                    s => s.Status == SagaState.StatusStarted
                        && s.CreatedAt < cutoff)
                .ToListAsync();
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

        private static bool IsActiveOperationDuplicate(MongoWriteException exception) =>
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey
            && exception.WriteError.Message.Contains(
                "uniq_active_booking_operation",
                StringComparison.Ordinal);

        private static FilterDefinition<SagaState> CurrentDispatchClaimFilter(
            Guid sagaId,
            DateTime claimTimestamp) =>
            Builders<SagaState>.Filter.And(
                Builders<SagaState>.Filter.Eq(s => s.SagaId, sagaId),
                Builders<SagaState>.Filter.Eq(
                    s => s.DispatchStatus,
                    SagaDispatchStatus.CLAIMED),
                Builders<SagaState>.Filter.Eq(
                    s => s.DispatchClaimedAt,
                    claimTimestamp));

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];

        private static void ValidateRequest(PaymentRequestedV1 request)
        {
            ArgumentNullException.ThrowIfNull(request);
            request.Validate();

            if (request.SchemaVersion != PaymentRequestedV1.CurrentSchemaVersion)
            {
                throw new ArgumentException(
                    $"Unsupported payment request schema version {request.SchemaVersion}.",
                    nameof(request));
            }
            if (request.SagaId == Guid.Empty)
            {
                throw new ArgumentException("SagaId is required.", nameof(request));
            }
            if (request.EscrowId == Guid.Empty)
            {
                throw new ArgumentException("EscrowId is required.", nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.BookingId))
            {
                throw new ArgumentException("BookingId is required.", nameof(request));
            }
            if (request.Amount <= 0)
            {
                throw new ArgumentException("Amount must be greater than zero.", nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length != 3)
            {
                throw new ArgumentException("Currency must be a three-letter code.", nameof(request));
            }
            if (string.IsNullOrWhiteSpace(request.PayerUserId)
                || string.IsNullOrWhiteSpace(request.PayeeUserId)
                || string.IsNullOrWhiteSpace(request.TaskMasterUserId))
            {
                throw new ArgumentException(
                    "PayerUserId, PayeeUserId, and TaskMasterUserId are required.",
                    nameof(request));
            }
            if (string.Equals(
                request.PayerUserId.Trim(),
                request.PayeeUserId.Trim(),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("PayerUserId and PayeeUserId must be different.", nameof(request));
            }
        }
    }
}
