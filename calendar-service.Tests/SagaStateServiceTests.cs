using calendar_service.Model;
using calendar_service.Services;
using calendar_service.Services.DAO;
using calendar_service.Services.Implementation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using Payment.Contracts.V1;
using System.Net;
using Xunit;

namespace calendar_service.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SagaStateService"/>. Follows the same Moq-based approach as
    /// <see cref="BookingServiceCreateTests"/>/<see cref="BookingServiceAcceptTests"/>: Moq
    /// stands in for <see cref="IMongoCollection{SagaState}"/>. We don't re-derive Mongo's own
    /// filter/update matching semantics in these tests (that's the driver's job, exercised
    /// against the real MongoDB instance in integration); instead each test stubs
    /// <c>InsertOneAsync</c>/<c>FindOneAndUpdateAsync</c>/<c>FindAsync</c> to return exactly the
    /// document(s) that call should produce, and asserts SagaStateService both calls the
    /// expected Mongo API and returns/maps that result correctly.
    /// </summary>
    public class SagaStateServiceTests
    {
        private static (SagaStateService svc, Mock<IMongoCollection<SagaState>> col)
            BuildService()
        {
            var col = new Mock<IMongoCollection<SagaState>>(MockBehavior.Loose);

            // Indexes.CreateOne is called in the constructor; hand back a loose mock.
            var indexes = new Mock<IMongoIndexManager<SagaState>>(MockBehavior.Loose);
            col.SetupGet(c => c.Indexes).Returns(indexes.Object);

            var db = new Mock<IMongoDBService>();
            db.Setup(d => d.GetCollection<SagaState>("SagaState")).Returns(col.Object);

            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var svc = new SagaStateService(db.Object, NullLogger<SagaStateService>.Instance, config);
            return (svc, col);
        }

        private static IAsyncCursor<SagaState> BuildCursor(List<SagaState> docs)
        {
            var cursor = new Mock<IAsyncCursor<SagaState>>();
            cursor.SetupGet(c => c.Current).Returns(docs);
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>())).Returns(true).Returns(false);
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(true).ReturnsAsync(false);
            return cursor.Object;
        }

        [Fact]
        public void Constructor_CreatesTtlIndexOnUpdatedAt_WithConfiguredRetention()
        {
            var col = new Mock<IMongoCollection<SagaState>>(MockBehavior.Loose);
            var indexes = new Mock<IMongoIndexManager<SagaState>>(MockBehavior.Loose);
            col.SetupGet(c => c.Indexes).Returns(indexes.Object);
            var db = new Mock<IMongoDBService>();
            db.Setup(d => d.GetCollection<SagaState>("SagaState")).Returns(col.Object);
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["SagaState:RetentionDays"] = "30" })
                .Build();

            CreateIndexModel<SagaState>? ttlModel = null;
            indexes.Setup(i => i.CreateOne(
                    It.Is<CreateIndexModel<SagaState>>(m => m.Options != null && m.Options.Name == "ttl_terminal_updatedat"),
                    It.IsAny<CreateOneIndexOptions>(),
                    It.IsAny<CancellationToken>()))
                .Callback<CreateIndexModel<SagaState>, CreateOneIndexOptions, CancellationToken>((m, _, _) => ttlModel = m)
                .Returns("ttl_terminal_updatedat");

            _ = new SagaStateService(db.Object, NullLogger<SagaStateService>.Instance, config);

            Assert.NotNull(ttlModel);
            Assert.Equal(TimeSpan.FromDays(30), ttlModel!.Options!.ExpireAfter);
            Assert.NotNull(ttlModel.Options.PartialFilterExpression);
        }

        [Fact]
        public void Constructor_TtlIndexCreationThrows_DoesNotPropagate()
        {
            // If a previous deploy already created the TTL index with different options, Mongo
            // rejects the CreateOne call. The service must not crash calendar-service's startup
            // over an index-tuning mismatch — it logs and moves on (see EnsureIndexes).
            var col = new Mock<IMongoCollection<SagaState>>(MockBehavior.Loose);
            var indexes = new Mock<IMongoIndexManager<SagaState>>(MockBehavior.Loose);
            col.SetupGet(c => c.Indexes).Returns(indexes.Object);
            var db = new Mock<IMongoDBService>();
            db.Setup(d => d.GetCollection<SagaState>("SagaState")).Returns(col.Object);
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            indexes.Setup(i => i.CreateOne(
                    It.Is<CreateIndexModel<SagaState>>(m => m.Options != null && m.Options.Name == "ttl_terminal_updatedat"),
                    It.IsAny<CreateOneIndexOptions>(),
                    It.IsAny<CancellationToken>()))
                .Throws(new MongoCommandException(
                    new MongoDB.Driver.Core.Connections.ConnectionId(new MongoDB.Driver.Core.Servers.ServerId(
                        new MongoDB.Driver.Core.Clusters.ClusterId(), new DnsEndPoint("localhost", 27017))),
                    "IndexOptionsConflict", new MongoDB.Bson.BsonDocument()));

            var exception = Record.Exception(() => new SagaStateService(db.Object, NullLogger<SagaStateService>.Instance, config));

            Assert.Null(exception);
        }

        [Fact]
        public void Constructor_CreatesBookingIdCreatedAtIndex()
        {
            var col = new Mock<IMongoCollection<SagaState>>(MockBehavior.Loose);
            var indexes = new Mock<IMongoIndexManager<SagaState>>(MockBehavior.Loose);
            col.SetupGet(c => c.Indexes).Returns(indexes.Object);
            var db = new Mock<IMongoDBService>();
            db.Setup(d => d.GetCollection<SagaState>("SagaState")).Returns(col.Object);
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            _ = new SagaStateService(db.Object, NullLogger<SagaStateService>.Instance, config);

            indexes.Verify(i => i.CreateOne(
                It.Is<CreateIndexModel<SagaState>>(m => m.Options != null && m.Options.Name == "bookingid_createdat_desc"),
                It.IsAny<CreateOneIndexOptions>(),
                It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public void Constructor_CreatesUniqueActiveBookingOperationIndex()
        {
            var col = new Mock<IMongoCollection<SagaState>>(MockBehavior.Loose);
            var indexes = new Mock<IMongoIndexManager<SagaState>>(MockBehavior.Loose);
            col.SetupGet(c => c.Indexes).Returns(indexes.Object);
            var db = new Mock<IMongoDBService>();
            db.Setup(d => d.GetCollection<SagaState>("SagaState")).Returns(col.Object);
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            _ = new SagaStateService(
                db.Object,
                NullLogger<SagaStateService>.Instance,
                config);

            indexes.Verify(i => i.CreateOne(
                    It.Is<CreateIndexModel<SagaState>>(model =>
                        model.Options != null
                        && model.Options.Name == "uniq_active_booking_operation"
                        && model.Options.Unique == true
                        && model.Options.PartialFilterExpression != null),
                    It.IsAny<CreateOneIndexOptions>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task StartAsync_InsertsStartedSagaWithGivenFields()
        {
            var (svc, col) = BuildService();
            var sagaId = Guid.NewGuid();
            SagaState? inserted = null;
            col.Setup(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()))
                .Callback<SagaState, InsertOneOptions, CancellationToken>((s, _, _) => inserted = s)
                .Returns(Task.CompletedTask);

            var saga = await svc.StartAsync("booking-1", sagaId, 42.50m);

            Assert.Equal(SagaState.StatusStarted, saga.Status);
            Assert.Equal("booking-1", saga.BookingId);
            Assert.Equal(sagaId, saga.SagaId);
            Assert.Equal(42.50m, saga.RequestedAmount);

            Assert.NotNull(inserted);
            Assert.Same(saga, inserted);
            col.Verify(c => c.InsertOneAsync(
                It.IsAny<SagaState>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task EnqueueAsync_InsertsStartedSagaAndPendingRequestAsOneDocument()
        {
            var (svc, col) = BuildService();
            var request = NewPaymentRequest(PaymentOperation.FundEscrow);
            SagaState? inserted = null;
            col.Setup(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()))
                .Callback<SagaState, InsertOneOptions, CancellationToken>(
                    (saga, _, _) => inserted = saga)
                .Returns(Task.CompletedTask);

            var saga = await svc.EnqueueAsync(
                request with
                {
                    BookingId = " booking-1 ",
                    Currency = "usd",
                    PayerUserId = " requester ",
                    PayeeUserId = " admin-custody ",
                    PaymentMethodToken = " pmt_token "
                },
                " 00-trace-parent ");

            Assert.Same(inserted, saga);
            Assert.Equal(SagaState.StatusStarted, saga.Status);
            Assert.Equal(request.SagaId, saga.SagaId);
            Assert.Equal(request.EscrowId, saga.EscrowId);
            Assert.Equal(PaymentOperation.FundEscrow, saga.Operation);
            Assert.Equal(SagaDispatchStatus.PENDING, saga.DispatchStatus);
            Assert.Equal(0, saga.DispatchAttemptCount);
            Assert.NotNull(saga.NextDispatchAttemptAt);
            Assert.Equal("00-trace-parent", saga.TraceParent);
            Assert.NotNull(saga.PaymentRequest);
            Assert.Equal("booking-1", saga.PaymentRequest!.BookingId);
            Assert.Equal("USD", saga.PaymentRequest.Currency);
            Assert.Equal("requester", saga.PaymentRequest.PayerUserId);
            Assert.Equal("admin-custody", saga.PaymentRequest.PayeeUserId);
            Assert.Equal("pmt_token", saga.PaymentRequest.PaymentMethodToken);
            Assert.Equal(request, saga.PaymentRequest.ToContract());
            col.Verify(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task EnqueueAsync_PersistenceFailure_PropagatesWithoutAnyFollowupWrite()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("Mongo unavailable"));

            var exception = await Assert.ThrowsAsync<SagaOutboxPersistenceException>(
                () => svc.EnqueueAsync(NewPaymentRequest(PaymentOperation.FundEscrow)));

            Assert.IsType<TimeoutException>(exception.InnerException);
            col.Verify(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            col.Verify(c => c.FindOneAndUpdateAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Theory]
        [InlineData(PaymentOperation.FundEscrow)]
        [InlineData(PaymentOperation.ReleaseEscrow)]
        [InlineData(PaymentOperation.RefundEscrow)]
        public async Task EnqueueAsync_ConcurrentSameBookingOperation_SecondRequestIsRejected(
            string operation)
        {
            var (svc, col) = BuildService();
            col.SetupSequence(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask)
                .ThrowsAsync(BuildDuplicateKeyMongoWriteException(
                    "uniq_active_booking_operation"));

            await svc.EnqueueAsync(NewPaymentRequest(operation));
            var exception = await Assert.ThrowsAsync<ActivePaymentSagaException>(
                () => svc.EnqueueAsync(NewPaymentRequest(operation)));

            Assert.Equal("booking-1", exception.BookingId);
            Assert.Equal(operation, exception.Operation);
        }

        [Fact]
        public async Task EnqueueAsync_DuplicateSagaId_DoesNotMisreportActiveOperation()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(BuildDuplicateKeyMongoWriteException("uniq_saga_id"));

            await Assert.ThrowsAsync<MongoWriteException>(
                () => svc.EnqueueAsync(NewPaymentRequest(PaymentOperation.FundEscrow)));
        }

        [Fact]
        public async Task EnqueueAsync_DifferentOperationsForSameBooking_AreAllowed()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            await svc.EnqueueAsync(NewPaymentRequest(PaymentOperation.ReleaseEscrow));
            await svc.EnqueueAsync(NewPaymentRequest(PaymentOperation.RefundEscrow));

            col.Verify(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }

        [Fact]
        public async Task EnqueueAsync_InvalidRequest_DoesNotWrite()
        {
            var (svc, col) = BuildService();
            var request = NewPaymentRequest(PaymentOperation.FundEscrow) with
            {
                PaymentMethodToken = null
            };

            await Assert.ThrowsAsync<ArgumentException>(
                () => svc.EnqueueAsync(request));

            col.Verify(c => c.InsertOneAsync(
                    It.IsAny<SagaState>(),
                    It.IsAny<InsertOneOptions>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task CompleteAsync_ExistingSaga_ReturnsCompletedWithTransactionId()
        {
            var (svc, col) = BuildService();
            var sagaId = Guid.NewGuid();
            var updated = new SagaState
            {
                Id = "1", SagaId = sagaId, BookingId = "booking-1",
                Status = SagaState.StatusCompleted, RequestedAmount = 10m,
                PaymentTransactionId = "txn-123"
            };
            col.Setup(c => c.FindOneAndUpdateAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(updated);

            var result = await svc.CompleteAsync(sagaId, "txn-123");

            Assert.Same(updated, result);
            Assert.Equal(SagaState.StatusCompleted, result!.Status);
            Assert.Equal("txn-123", result.PaymentTransactionId);
            col.Verify(c => c.FindOneAndUpdateAsync(
                It.IsAny<FilterDefinition<SagaState>>(),
                It.IsAny<UpdateDefinition<SagaState>>(),
                It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task CompleteAsync_UnknownSagaId_ReturnsNull()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.FindOneAndUpdateAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((SagaState?)null);

            var result = await svc.CompleteAsync(Guid.NewGuid(), "txn-123");

            Assert.Null(result);
        }

        [Fact]
        public async Task FailAsync_ExistingSaga_ReturnsFailedWithReason()
        {
            var (svc, col) = BuildService();
            var sagaId = Guid.NewGuid();
            var updated = new SagaState
            {
                Id = "1", SagaId = sagaId, BookingId = "booking-1",
                Status = SagaState.StatusFailed, RequestedAmount = 10m,
                FailureReason = "Payment declined"
            };
            col.Setup(c => c.FindOneAndUpdateAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(updated);

            var result = await svc.FailAsync(sagaId, "Payment declined");

            Assert.Same(updated, result);
            Assert.Equal(SagaState.StatusFailed, result!.Status);
            Assert.Equal("Payment declined", result.FailureReason);
        }

        [Fact]
        public async Task FailAsync_UnknownSagaId_ReturnsNull()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.FindOneAndUpdateAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((SagaState?)null);

            var result = await svc.FailAsync(Guid.NewGuid(), "unreachable");

            Assert.Null(result);
        }

        [Fact]
        public async Task GetBySagaIdAsync_ReturnsWhateverCollectionYields()
        {
            var (svc, col) = BuildService();
            var sagaId = Guid.NewGuid();
            var existing = new SagaState { Id = "1", SagaId = sagaId, BookingId = "booking-1", Status = SagaState.StatusStarted, RequestedAmount = 10m };
            col.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<FindOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => BuildCursor(new List<SagaState> { existing }));

            var result = await svc.GetBySagaIdAsync(sagaId);

            Assert.Same(existing, result);
        }

        [Fact]
        public async Task GetBySagaIdAsync_NoMatch_ReturnsNull()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<FindOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => BuildCursor(new List<SagaState>()));

            var result = await svc.GetBySagaIdAsync(Guid.NewGuid());

            Assert.Null(result);
        }

        [Fact]
        public async Task GetLatestByBookingIdAsync_ReturnsWhateverCollectionYields()
        {
            var (svc, col) = BuildService();
            var latest = new SagaState { Id = "1", SagaId = Guid.NewGuid(), BookingId = "booking-1", Status = SagaState.StatusStarted, RequestedAmount = 10m };
            col.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<FindOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => BuildCursor(new List<SagaState> { latest }));

            var result = await svc.GetLatestByBookingIdAsync("booking-1");

            Assert.Same(latest, result);
        }

        [Fact]
        public async Task GetLatestByBookingIdAsync_NoSagaForBooking_ReturnsNull()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<FindOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => BuildCursor(new List<SagaState>()));

            var result = await svc.GetLatestByBookingIdAsync("booking-none");

            Assert.Null(result);
        }

        [Fact]
        public async Task FindStuckAsync_ReturnsDocumentsTheCollectionYields()
        {
            // The actual STARTED + createdAt-cutoff filtering is applied by MongoDB itself in
            // production (see SagaStateService.FindStuckAsync); here we verify the service
            // issues the query and maps the cursor back into a List<SagaState> correctly.
            var (svc, col) = BuildService();
            var stuck = new SagaState { Id = "1", SagaId = Guid.NewGuid(), BookingId = "booking-stuck", Status = SagaState.StatusStarted, RequestedAmount = 10m };
            col.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<FindOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => BuildCursor(new List<SagaState> { stuck }));

            var result = await svc.FindStuckAsync(TimeSpan.FromSeconds(30));

            Assert.Single(result);
            Assert.Equal("booking-stuck", result[0].BookingId);
            col.Verify(c => c.FindAsync(
                It.IsAny<FilterDefinition<SagaState>>(),
                It.IsAny<FindOptions<SagaState, SagaState>>(),
                It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task FindStuckAsync_NoStuckSagas_ReturnsEmpty()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<FindOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => BuildCursor(new List<SagaState>()));

            var result = await svc.FindStuckAsync(TimeSpan.FromSeconds(30));

            Assert.Empty(result);
        }

        [Fact]
        public async Task TryClaimAsync_Unclaimed_ReturnsClaimedSaga()
        {
            // Simulates Mongo actually matching+updating the document: the filter/update
            // semantics themselves are exercised by the driver against real MongoDB in
            // integration, not re-derived here (same approach as the rest of this file).
            var (svc, col) = BuildService();
            var sagaId = Guid.NewGuid();
            var claimed = new SagaState
            {
                Id = "1", SagaId = sagaId, BookingId = "booking-1",
                Status = SagaState.StatusStarted, RequestedAmount = 10m,
                ReconciliationClaimedAt = DateTime.UtcNow
            };
            col.Setup(c => c.FindOneAndUpdateAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(claimed);

            var result = await svc.TryClaimAsync(sagaId, TimeSpan.FromSeconds(45));

            Assert.Same(claimed, result);
            Assert.NotNull(result!.ReconciliationClaimedAt);
        }

        [Fact]
        public async Task TryClaimAsync_AlreadyClaimedByAnotherInstance_ReturnsNull()
        {
            // A live claim held by another replica means the filter (Status == STARTED &&
            // (unclaimed || claim stale)) simply won't match in Mongo, so FindOneAndUpdate
            // returns null — verify TryClaimAsync surfaces that as "couldn't claim" rather than
            // throwing or fabricating a result.
            var (svc, col) = BuildService();
            col.Setup(c => c.FindOneAndUpdateAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((SagaState?)null);

            var result = await svc.TryClaimAsync(Guid.NewGuid(), TimeSpan.FromSeconds(45));

            Assert.Null(result);
        }

        [Fact]
        public async Task TryClaimNextDispatchAsync_EligibleRequest_ReturnsClaimedSaga()
        {
            var (svc, col) = BuildService();
            var claimed = new SagaState
            {
                SagaId = Guid.NewGuid(),
                BookingId = "booking-1",
                Status = SagaState.StatusStarted,
                DispatchStatus = SagaDispatchStatus.CLAIMED,
                DispatchAttemptCount = 1,
                DispatchClaimedAt = DateTime.UtcNow,
                DispatchClaimExpiresAt = DateTime.UtcNow.AddSeconds(30),
                PaymentRequest = PendingPaymentRequest.FromContract(
                    NewPaymentRequest(PaymentOperation.FundEscrow))
            };
            FindOneAndUpdateOptions<SagaState, SagaState>? options = null;
            col.Setup(c => c.FindOneAndUpdateAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<FilterDefinition<SagaState>, UpdateDefinition<SagaState>,
                    FindOneAndUpdateOptions<SagaState, SagaState>, CancellationToken>(
                    (_, _, capturedOptions, _) => options = capturedOptions)
                .ReturnsAsync(claimed);

            var result = await svc.TryClaimNextDispatchAsync(
                TimeSpan.FromSeconds(30));

            Assert.Same(claimed, result);
            Assert.Equal(SagaDispatchStatus.CLAIMED, result!.DispatchStatus);
            Assert.Equal(1, result.DispatchAttemptCount);
            Assert.NotNull(result.DispatchClaimedAt);
            Assert.NotNull(result.DispatchClaimExpiresAt);
            Assert.NotNull(options?.Sort);
            Assert.Equal(ReturnDocument.After, options?.ReturnDocument);
        }

        [Fact]
        public async Task TryClaimNextDispatchAsync_NoEligibleRequest_ReturnsNull()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.FindOneAndUpdateAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<FindOneAndUpdateOptions<SagaState, SagaState>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((SagaState?)null);

            var result = await svc.TryClaimNextDispatchAsync(
                TimeSpan.FromSeconds(30));

            Assert.Null(result);
        }

        [Fact]
        public async Task MarkDispatchedAsync_CurrentClaim_ReturnsTrue()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<UpdateOptions>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResultWithModifiedCount(1));

            var result = await svc.MarkDispatchedAsync(
                Guid.NewGuid(),
                DateTime.UtcNow);

            Assert.True(result);
        }

        [Fact]
        public async Task MarkDispatchedAsync_StaleClaim_ReturnsFalse()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<UpdateOptions>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResultWithModifiedCount(0));

            var result = await svc.MarkDispatchedAsync(
                Guid.NewGuid(),
                DateTime.UtcNow.AddMinutes(-1));

            Assert.False(result);
        }

        [Fact]
        public async Task RescheduleDispatchAsync_CurrentClaim_ReturnsTrue()
        {
            var (svc, col) = BuildService();
            col.Setup(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<SagaState>>(),
                    It.IsAny<UpdateDefinition<SagaState>>(),
                    It.IsAny<UpdateOptions>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResultWithModifiedCount(1));

            var result = await svc.RescheduleDispatchAsync(
                Guid.NewGuid(),
                DateTime.UtcNow,
                DateTime.UtcNow.AddSeconds(5),
                "Kafka unavailable");

            Assert.True(result);
        }

        private static PaymentRequestedV1 NewPaymentRequest(string operation)
        {
            var isFunding = operation == PaymentOperation.FundEscrow;
            return new PaymentRequestedV1
            {
                SagaId = Guid.NewGuid(),
                EscrowId = Guid.NewGuid(),
                BookingId = "booking-1",
                Operation = operation,
                Amount = 100m,
                Currency = "USD",
                PayerUserId = isFunding ? "requester" : "admin-custody",
                PayeeUserId = operation == PaymentOperation.ReleaseEscrow
                    ? "taskmaster"
                    : isFunding
                        ? "admin-custody"
                        : "requester",
                TaskMasterUserId = "taskmaster",
                PaymentMethodToken = isFunding ? "pmt_token" : null
            };
        }

        private static MongoWriteException BuildDuplicateKeyMongoWriteException(
            string indexName)
        {
            var writeError = (WriteError)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(WriteError));
            SetBackingField(writeError, "_category", ServerErrorCategory.DuplicateKey);
            SetBackingField(writeError, "_code", 11000);
            SetBackingField(
                writeError,
                "_message",
                $"E11000 duplicate key error index: {indexName}");

            var exception = (MongoWriteException)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(MongoWriteException));
            SetBackingField(exception, "_writeError", writeError);
            return exception;
        }

        private static UpdateResult UpdateResultWithModifiedCount(long count)
        {
            var result = new Mock<UpdateResult>();
            result.SetupGet(r => r.IsAcknowledged).Returns(true);
            result.SetupGet(r => r.MatchedCount).Returns(count);
            result.SetupGet(r => r.ModifiedCount).Returns(count);
            return result.Object;
        }

        private static void SetBackingField(
            object target,
            string fieldName,
            object? value)
        {
            var type = target.GetType();
            while (type != null)
            {
                var field = type.GetField(
                    fieldName,
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }

                type = type.BaseType;
            }

            throw new InvalidOperationException(
                $"Field '{fieldName}' was not found on {target.GetType().Name}.");
        }
    }
}
