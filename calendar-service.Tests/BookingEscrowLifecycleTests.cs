using calendar_service.Model;
using calendar_service.Services.DAO;
using calendar_service.Services.Implementation;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using Payment.Contracts.V1;
using Xunit;

namespace calendar_service.Tests
{
    public class BookingEscrowLifecycleTests
    {
        private const string Requester = "alice";
        private const string TaskMaster = "owner";

        [Fact]
        public void FixAgreedPrice_UsesAcceptedOfferAndCurrency()
        {
            var booking = BookingWith(status: Booking.StatusPending);
            booking.DurationHours = 3;
            booking.OfferedRatePerHour = 33.335m;

            booking.FixAgreedPrice();

            Assert.Equal(100.00m, booking.AgreedAmount);
            Assert.Equal("USD", booking.AgreedCurrency);
        }

        [Fact]
        public async Task AttachEscrowAsync_WithoutFixedPrice_Throws()
        {
            var booking = BookingWith(status: Booking.StatusAccepted);
            var (service, collection) = BuildService(booking);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.AttachEscrowAsync("booking-1", Requester, Guid.NewGuid()));

            Assert.Contains("fixed", exception.Message, StringComparison.OrdinalIgnoreCase);
            VerifyNoUpdate(collection);
        }

        [Fact]
        public async Task StartWorkAsync_PendingEscrow_Throws()
        {
            var booking = BookingWith(
                status: Booking.StatusAccepted,
                escrowStatus: EscrowStatus.Pending);
            var (service, collection) = BuildService(booking);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartWorkAsync("booking-1", TaskMaster));

            Assert.Contains("FUNDED", exception.Message);
            VerifyNoUpdate(collection);
        }

        [Fact]
        public async Task StartWorkAsync_AfterRefundRequest_Throws()
        {
            var booking = BookingWith(
                status: Booking.StatusAccepted,
                escrowStatus: EscrowStatus.Funded);
            booking.RefundRequestedAt = DateTime.UtcNow;
            var (service, collection) = BuildService(booking);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartWorkAsync("booking-1", TaskMaster));

            Assert.Contains("refund", exception.Message, StringComparison.OrdinalIgnoreCase);
            VerifyNoUpdate(collection);
        }

        [Fact]
        public async Task StartWorkAsync_FundedEscrow_MovesBookingToInProgress()
        {
            var existing = BookingWith(
                status: Booking.StatusAccepted,
                escrowStatus: EscrowStatus.Funded);
            var updated = BookingWith(
                status: Booking.StatusInProgress,
                escrowStatus: EscrowStatus.Funded);
            updated.WorkStartedAt = DateTime.UtcNow;
            var (service, collection) = BuildService(existing, updated);
            SetupSuccessfulUpdate(collection);

            var result = await service.StartWorkAsync("booking-1", TaskMaster);

            Assert.Equal(Booking.StatusInProgress, result.Status);
            Assert.NotNull(result.WorkStartedAt);
            VerifyOneUpdate(collection);
        }

        [Fact]
        public async Task RequestEscrowReleaseAsync_BeforeWorkStarts_Throws()
        {
            var booking = BookingWith(
                status: Booking.StatusAccepted,
                escrowStatus: EscrowStatus.Funded);
            var (service, collection) = BuildService(booking);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RequestEscrowReleaseAsync("booking-1", TaskMaster, "proof.jpg"));

            VerifyNoUpdate(collection);
        }

        [Fact]
        public async Task RequestEscrowReleaseAsync_FundedInProgressBooking_UsesFixedAmount()
        {
            var existing = BookingWith(
                status: Booking.StatusInProgress,
                escrowStatus: EscrowStatus.Funded);
            existing.AgreedAmount = 75m;
            existing.AgreedCurrency = "USD";

            var updated = BookingWith(
                status: Booking.StatusImplemented,
                escrowStatus: EscrowStatus.Funded);
            updated.AgreedAmount = 75m;
            updated.AgreedCurrency = "USD";
            updated.InvoiceAmount = 75m;
            updated.ProofFileUrl = "proof.jpg";
            updated.ReleaseRequestedAt = DateTime.UtcNow;

            var (service, collection) = BuildService(existing, updated);
            SetupSuccessfulUpdate(collection);

            var result = await service.RequestEscrowReleaseAsync(
                "booking-1",
                TaskMaster,
                " proof.jpg ");

            Assert.Equal(75m, result.InvoiceAmount);
            Assert.Equal("proof.jpg", result.ProofFileUrl);
            Assert.NotNull(result.ReleaseRequestedAt);
            VerifyOneUpdate(collection);
        }

        [Fact]
        public async Task RequestEscrowReleaseAsync_PersistedMatchingIntent_ReturnsWithoutUpdate()
        {
            var existing = BookingWith(
                status: Booking.StatusImplemented,
                escrowStatus: EscrowStatus.Funded);
            existing.AgreedAmount = 75m;
            existing.InvoiceAmount = 75m;
            existing.ProofFileUrl = "proof.jpg";
            existing.ReleaseRequestedAt = DateTime.UtcNow;
            var (service, collection) = BuildService(existing);

            var result = await service.RequestEscrowReleaseAsync(
                "booking-1",
                TaskMaster,
                " proof.jpg ");

            Assert.Same(existing, result);
            VerifyNoUpdate(collection);
        }

        [Fact]
        public async Task RequestCancellationAsync_UnfundedBooking_CancelsWithoutRefund()
        {
            var existing = BookingWith(status: Booking.StatusAccepted);
            var updated = BookingWith(status: Booking.StatusCancelled);
            updated.CancelledAt = DateTime.UtcNow;
            var (service, collection) = BuildService(existing, updated);
            SetupSuccessfulUpdate(collection);

            var result = await service.RequestCancellationAsync("booking-1", Requester);

            Assert.Equal(Booking.StatusCancelled, result.Status);
            Assert.NotNull(result.CancelledAt);
            Assert.Null(result.RefundRequestedAt);
        }

        [Fact]
        public async Task RequestCancellationAsync_WhileFundingPending_Throws()
        {
            var existing = BookingWith(
                status: Booking.StatusAccepted,
                escrowStatus: EscrowStatus.Pending);
            var (service, collection) = BuildService(existing);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RequestCancellationAsync("booking-1", Requester));

            Assert.Contains("FUNDED", exception.Message);
            VerifyNoUpdate(collection);
        }

        [Fact]
        public async Task RequestCancellationAsync_FundedBeforeWork_RequestsRefund()
        {
            var existing = BookingWith(
                status: Booking.StatusAccepted,
                escrowStatus: EscrowStatus.Funded);
            existing.AgreedAmount = 100m;
            existing.AgreedCurrency = "USD";
            var updated = BookingWith(
                status: Booking.StatusAccepted,
                escrowStatus: EscrowStatus.Funded);
            updated.AgreedAmount = 100m;
            updated.AgreedCurrency = "USD";
            updated.RefundRequestedAt = DateTime.UtcNow;
            var (service, collection) = BuildService(existing, updated);
            SetupSuccessfulUpdate(collection);

            var result = await service.RequestCancellationAsync("booking-1", Requester);

            Assert.Equal(Booking.StatusAccepted, result.Status);
            Assert.NotNull(result.RefundRequestedAt);
            Assert.Null(result.CancelledAt);
        }

        [Fact]
        public async Task RequestCancellationAsync_PersistedRefundIntent_ReturnsWithoutUpdate()
        {
            var existing = BookingWith(
                status: Booking.StatusAccepted,
                escrowStatus: EscrowStatus.Funded);
            existing.RefundRequestedAt = DateTime.UtcNow;
            var (service, collection) = BuildService(existing);

            var result = await service.RequestCancellationAsync(
                "booking-1",
                Requester);

            Assert.Same(existing, result);
            VerifyNoUpdate(collection);
        }

        [Fact]
        public async Task RequestCancellationAsync_FundedWithoutFixedPrice_DoesNotPersistRefundIntent()
        {
            var existing = BookingWith(
                status: Booking.StatusAccepted,
                escrowStatus: EscrowStatus.Funded);
            existing.AgreedAmount = null;
            existing.AgreedCurrency = null;
            var (service, collection) = BuildService(existing);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RequestCancellationAsync(
                    "booking-1",
                    Requester));

            Assert.Contains("price", exception.Message);
            VerifyNoUpdate(collection);
        }

        [Theory]
        [InlineData(Booking.StatusInProgress, EscrowStatus.Funded)]
        [InlineData(Booking.StatusImplemented, EscrowStatus.Funded)]
        [InlineData(Booking.StatusCompleted, EscrowStatus.Released)]
        [InlineData(Booking.StatusCancelled, EscrowStatus.Refunded)]
        public async Task RequestCancellationAsync_AfterWorkOrTerminalTransfer_Throws(
            string bookingStatus,
            string escrowStatus)
        {
            var booking = BookingWith(bookingStatus, escrowStatus);
            var (service, collection) = BuildService(booking);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RequestCancellationAsync("booking-1", Requester));

            VerifyNoUpdate(collection);
        }

        [Fact]
        public async Task MarkEscrowFundedAsync_DuplicateResultIsRejected()
        {
            var (service, collection) = BuildService();
            collection.Setup(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<Booking>>(),
                    It.IsAny<UpdateDefinition<Booking>>(),
                    It.IsAny<UpdateOptions>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResultWith(0, 0));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.MarkEscrowFundedAsync("booking-1", Guid.NewGuid()));
        }

        [Theory]
        [InlineData(PaymentOperation.FundEscrow, Booking.StatusAccepted, EscrowStatus.Pending, Booking.StatusAccepted, EscrowStatus.Funded)]
        [InlineData(PaymentOperation.ReleaseEscrow, Booking.StatusImplemented, EscrowStatus.Funded, Booking.StatusCompleted, EscrowStatus.Released)]
        [InlineData(PaymentOperation.RefundEscrow, Booking.StatusAccepted, EscrowStatus.Funded, Booking.StatusCancelled, EscrowStatus.Refunded)]
        public async Task ApplyApprovedPaymentResultAsync_ValidState_AppliesTransition(
            string operation,
            string initialStatus,
            string initialEscrowStatus,
            string finalStatus,
            string finalEscrowStatus)
        {
            var escrowId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var existing = BookingWith(initialStatus, initialEscrowStatus);
            existing.EscrowId = escrowId;
            existing.ReleaseRequestedAt =
                operation == PaymentOperation.ReleaseEscrow ? DateTime.UtcNow : null;
            existing.RefundRequestedAt =
                operation == PaymentOperation.RefundEscrow ? DateTime.UtcNow : null;
            var updated = BookingWith(finalStatus, finalEscrowStatus);
            updated.EscrowId = escrowId;
            updated.PaymentTransactionId = transactionId.ToString("D");
            var (service, collection) = BuildService(existing, updated);
            SetupSuccessfulUpdate(collection);

            var application = await service.ApplyApprovedPaymentResultAsync(
                PaymentResult(
                    operation,
                    escrowId,
                    transactionId));

            Assert.Equal(PaymentResultApplicationOutcome.Applied, application.Outcome);
            Assert.Equal(finalStatus, application.Booking.Status);
            Assert.Equal(finalEscrowStatus, application.Booking.EscrowStatus);
            Assert.Equal(
                transactionId.ToString("D"),
                application.Booking.PaymentTransactionId);
            VerifyOneUpdate(collection);
        }

        [Fact]
        public async Task ApplyApprovedPaymentResultAsync_ExactTerminalDuplicate_DoesNotUpdate()
        {
            var escrowId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var existing = BookingWith(
                Booking.StatusCompleted,
                EscrowStatus.Released);
            existing.EscrowId = escrowId;
            existing.PaymentTransactionId = transactionId.ToString("D");
            var (service, collection) = BuildService(existing);

            var application = await service.ApplyApprovedPaymentResultAsync(
                PaymentResult(
                    PaymentOperation.ReleaseEscrow,
                    escrowId,
                    transactionId));

            Assert.Equal(
                PaymentResultApplicationOutcome.AlreadyApplied,
                application.Outcome);
            VerifyNoUpdate(collection);
        }

        private static PaymentResultV1 PaymentResult(
            string operation,
            Guid escrowId,
            Guid transactionId) => new()
        {
            SagaId = Guid.NewGuid(),
            EscrowId = escrowId,
            BookingId = "booking-1",
            Operation = operation,
            TransactionId = transactionId,
            Amount = 100m,
            Currency = "USD",
            Status = PaymentResultV1.StatusApproved
        };

        private static Booking BookingWith(string status, string? escrowStatus = null) => new()
        {
            Id = "booking-1",
            TaskMasterId = "taskmaster-1",
            TaskMasterUsername = TaskMaster,
            RequesterUsername = Requester,
            SlotStart = DateTime.UtcNow.AddDays(1),
            DurationHours = 1,
            Status = status,
            EscrowId = escrowStatus == null ? null : Guid.NewGuid(),
            EscrowStatus = escrowStatus
        };

        private static (BookingService Service, Mock<IMongoCollection<Booking>> Collection)
            BuildService(params Booking[] findResults)
        {
            var queue = new Queue<Booking>(findResults);
            var collection = new Mock<IMongoCollection<Booking>>(MockBehavior.Loose);
            var indexes = new Mock<IMongoIndexManager<Booking>>(MockBehavior.Loose);
            collection.SetupGet(c => c.Indexes).Returns(indexes.Object);
            collection.Setup(c => c.FindAsync(
                    It.IsAny<FilterDefinition<Booking>>(),
                    It.IsAny<FindOptions<Booking, Booking>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var current = queue.Count == 0
                        ? Array.Empty<Booking>()
                        : new[] { queue.Dequeue() };
                    return BuildCursor(current);
                });

            var database = new Mock<IMongoDBService>();
            database.Setup(d => d.GetCollection<Booking>("Booking"))
                .Returns(collection.Object);
            return (
                new BookingService(database.Object, NullLogger<BookingService>.Instance),
                collection);
        }

        private static IAsyncCursor<Booking> BuildCursor(IEnumerable<Booking> bookings)
        {
            var cursor = new Mock<IAsyncCursor<Booking>>();
            cursor.SetupGet(c => c.Current).Returns(bookings);
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>()))
                .Returns(true)
                .Returns(false);
            cursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(true)
                .ReturnsAsync(false);
            return cursor.Object;
        }

        private static void SetupSuccessfulUpdate(Mock<IMongoCollection<Booking>> collection)
        {
            collection.Setup(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<Booking>>(),
                    It.IsAny<UpdateDefinition<Booking>>(),
                    It.IsAny<UpdateOptions>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResultWith(1, 1));
        }

        private static UpdateResult UpdateResultWith(long matchedCount, long modifiedCount)
        {
            var result = new Mock<UpdateResult>();
            result.SetupGet(value => value.IsAcknowledged).Returns(true);
            result.SetupGet(value => value.MatchedCount).Returns(matchedCount);
            result.SetupGet(value => value.ModifiedCount).Returns(modifiedCount);
            return result.Object;
        }

        private static void VerifyNoUpdate(Mock<IMongoCollection<Booking>> collection) =>
            collection.Verify(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<Booking>>(),
                    It.IsAny<UpdateDefinition<Booking>>(),
                    It.IsAny<UpdateOptions>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);

        private static void VerifyOneUpdate(Mock<IMongoCollection<Booking>> collection) =>
            collection.Verify(c => c.UpdateOneAsync(
                    It.IsAny<FilterDefinition<Booking>>(),
                    It.IsAny<UpdateDefinition<Booking>>(),
                    It.IsAny<UpdateOptions>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
    }
}
