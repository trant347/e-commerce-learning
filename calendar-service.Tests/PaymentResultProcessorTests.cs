using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services;
using calendar_service.Services.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Payment.Contracts.V1;
using Xunit;

namespace calendar_service.Tests
{
    public class PaymentResultProcessorTests
    {
        [Fact]
        public async Task ProcessAsync_ApprovedFunding_FundsBookingAndNotifiesBothParties()
        {
            var context = BuildContext(PaymentOperation.FundEscrow);
            var booking = FinalBooking(context.Result, EscrowStatus.Funded);
            context.Bookings.Setup(service => service.ApplyApprovedPaymentResultAsync(
                    context.Result,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentResultApplication(
                    booking,
                    PaymentResultApplicationOutcome.Applied));

            var outcome = await context.Processor.ProcessAsync(context.Result);

            Assert.Equal(PaymentResultProcessingOutcome.Applied, outcome);
            Assert.Collection(
                context.Published,
                notification =>
                {
                    Assert.Equal("BOOKING_ESCROW_FUNDED", notification.Type);
                    Assert.Equal("requester", notification.RecipientUsername);
                },
                notification =>
                {
                    Assert.Equal("BOOKING_ESCROW_FUNDED", notification.Type);
                    Assert.Equal("taskmaster", notification.RecipientUsername);
                });
            VerifyCompleted(context);
        }

        [Fact]
        public async Task ProcessAsync_ApprovedRelease_CompletesBookingAndNotifiesTaskMaster()
        {
            var context = BuildContext(PaymentOperation.ReleaseEscrow);
            var booking = FinalBooking(
                context.Result,
                EscrowStatus.Released,
                Booking.StatusCompleted);
            context.Bookings.Setup(service => service.ApplyApprovedPaymentResultAsync(
                    context.Result,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentResultApplication(
                    booking,
                    PaymentResultApplicationOutcome.Applied));

            var outcome = await context.Processor.ProcessAsync(context.Result);

            Assert.Equal(PaymentResultProcessingOutcome.Applied, outcome);
            var notification = Assert.Single(context.Published);
            Assert.Equal("BOOKING_ESCROW_RELEASED", notification.Type);
            Assert.Equal("taskmaster", notification.RecipientUsername);
            VerifyCompleted(context);
        }

        [Fact]
        public async Task ProcessAsync_ApprovedRefund_CancelsBookingAndNotifiesRequester()
        {
            var context = BuildContext(PaymentOperation.RefundEscrow);
            var booking = FinalBooking(
                context.Result,
                EscrowStatus.Refunded,
                Booking.StatusCancelled);
            context.Bookings.Setup(service => service.ApplyApprovedPaymentResultAsync(
                    context.Result,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentResultApplication(
                    booking,
                    PaymentResultApplicationOutcome.Applied));

            var outcome = await context.Processor.ProcessAsync(context.Result);

            Assert.Equal(PaymentResultProcessingOutcome.Applied, outcome);
            var notification = Assert.Single(context.Published);
            Assert.Equal("BOOKING_ESCROW_REFUNDED", notification.Type);
            Assert.Equal("requester", notification.RecipientUsername);
            VerifyCompleted(context);
        }

        [Fact]
        public async Task ProcessAsync_Declined_FailsOnlySaga()
        {
            var context = BuildContext(PaymentOperation.FundEscrow);
            context.Result = context.Result with
            {
                Status = PaymentResultV1.StatusDeclined,
                DeclineReason = "Insufficient funds"
            };

            var outcome = await context.Processor.ProcessAsync(context.Result);

            Assert.Equal(PaymentResultProcessingOutcome.Declined, outcome);
            context.Sagas.Verify(service => service.FailResultAsync(
                    context.Result.SagaId,
                    context.Result.TransactionId.ToString("D"),
                    "Insufficient funds",
                    It.IsAny<CancellationToken>()),
                Times.Once);
            context.Bookings.Verify(service => service.ApplyApprovedPaymentResultAsync(
                    It.IsAny<PaymentResultV1>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Empty(context.Published);
        }

        [Fact]
        public async Task ProcessAsync_MismatchedAmount_FailsSagaWithoutChangingBooking()
        {
            var context = BuildContext(PaymentOperation.FundEscrow);
            context.Result = context.Result with { Amount = 99m };

            var outcome = await context.Processor.ProcessAsync(context.Result);

            Assert.Equal(PaymentResultProcessingOutcome.Mismatched, outcome);
            context.Sagas.Verify(service => service.FailResultAsync(
                    context.Result.SagaId,
                    context.Result.TransactionId.ToString("D"),
                    It.Is<string>(reason => reason.Contains("amount")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            context.Bookings.VerifyNoOtherCalls();
            Assert.Empty(context.Published);
        }

        [Fact]
        public async Task ProcessAsync_DuplicateMismatchedResult_IsIgnored()
        {
            var context = BuildContext(PaymentOperation.FundEscrow);
            context.Result = context.Result with { Amount = 99m };
            context.Saga.Status = SagaState.StatusFailed;
            context.Saga.PaymentTransactionId =
                context.Result.TransactionId.ToString("D");

            var outcome = await context.Processor.ProcessAsync(context.Result);

            Assert.Equal(PaymentResultProcessingOutcome.Duplicate, outcome);
            context.Bookings.VerifyNoOtherCalls();
            context.Sagas.Verify(service => service.FailResultAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessAsync_DuplicateCompletedResult_RepeatsNoSideEffects()
        {
            var context = BuildContext(PaymentOperation.ReleaseEscrow);
            context.Saga.Status = SagaState.StatusCompleted;
            context.Saga.PaymentTransactionId =
                context.Result.TransactionId.ToString("D");

            var outcome = await context.Processor.ProcessAsync(context.Result);

            Assert.Equal(PaymentResultProcessingOutcome.Duplicate, outcome);
            context.Bookings.VerifyNoOtherCalls();
            context.Sagas.Verify(service => service.CompleteResultAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Empty(context.Published);
        }

        [Fact]
        public async Task ProcessAsync_BookingAlreadyApplied_CompletesSagaWithoutNotifications()
        {
            var context = BuildContext(PaymentOperation.FundEscrow);
            var booking = FinalBooking(context.Result, EscrowStatus.Funded);
            context.Bookings.Setup(service => service.ApplyApprovedPaymentResultAsync(
                    context.Result,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentResultApplication(
                    booking,
                    PaymentResultApplicationOutcome.AlreadyApplied));

            var outcome = await context.Processor.ProcessAsync(context.Result);

            Assert.Equal(PaymentResultProcessingOutcome.Applied, outcome);
            VerifyCompleted(context);
            Assert.Empty(context.Published);
        }

        [Fact]
        public async Task ProcessAsync_OutOfOrderResult_RemainsRetryable()
        {
            var context = BuildContext(PaymentOperation.ReleaseEscrow);
            context.Bookings.Setup(service => service.ApplyApprovedPaymentResultAsync(
                    context.Result,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PaymentResultRetryableException(
                    "Booking is not ready for release."));

            await Assert.ThrowsAsync<PaymentResultRetryableException>(
                () => context.Processor.ProcessAsync(context.Result));

            context.Sagas.Verify(service => service.CompleteResultAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            context.Sagas.Verify(service => service.FailResultAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Empty(context.Published);
        }

        private static TestContext BuildContext(string operation)
        {
            var request = new PaymentRequestedV1
            {
                SagaId = Guid.NewGuid(),
                EscrowId = Guid.NewGuid(),
                BookingId = "booking-1",
                Operation = operation,
                Amount = 100m,
                Currency = "USD",
                PayerUserId = "requester",
                PayeeUserId = "admin-custody",
                TaskMasterUserId = "taskmaster",
                PaymentMethodToken = operation == PaymentOperation.FundEscrow
                    ? "pmt_token"
                    : null
            };
            var result = new PaymentResultV1
            {
                SagaId = request.SagaId,
                EscrowId = request.EscrowId,
                BookingId = request.BookingId,
                Operation = request.Operation,
                TransactionId = Guid.NewGuid(),
                Amount = request.Amount,
                Currency = request.Currency,
                Status = PaymentResultV1.StatusApproved
            };
            var saga = new SagaState
            {
                SagaId = request.SagaId,
                EscrowId = request.EscrowId,
                BookingId = request.BookingId,
                Operation = request.Operation,
                RequestedAmount = request.Amount,
                PaymentRequest = PendingPaymentRequest.FromContract(request),
                Status = SagaState.StatusStarted
            };
            var sagas = new Mock<ISagaStateService>();
            sagas.Setup(service => service.GetBySagaIdAsync(saga.SagaId))
                .ReturnsAsync(() => saga);
            sagas.Setup(service => service.CompleteResultAsync(
                    saga.SagaId,
                    result.TransactionId.ToString("D"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            sagas.Setup(service => service.FailResultAsync(
                    saga.SagaId,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var bookings = new Mock<IBookingService>();
            var notifications = new Mock<INotificationProducer>();
            var published = new List<BookingNotification>();
            notifications.Setup(producer => producer.PublishAsync(
                    It.IsAny<object>()))
                .Callback<object>(payload =>
                    published.Add(Assert.IsType<BookingNotification>(payload)))
                .Returns(Task.CompletedTask);
            var processor = new PaymentResultProcessor(
                sagas.Object,
                bookings.Object,
                notifications.Object,
                NullLogger<PaymentResultProcessor>.Instance);
            return new TestContext(
                processor,
                sagas,
                bookings,
                saga,
                result,
                published);
        }

        private static Booking FinalBooking(
            PaymentResultV1 result,
            string escrowStatus,
            string status = Booking.StatusAccepted) => new()
        {
            Id = result.BookingId,
            TaskMasterId = "taskmaster-id",
            TaskMasterUsername = "taskmaster",
            RequesterUsername = "requester",
            EscrowId = result.EscrowId,
            EscrowStatus = escrowStatus,
            Status = status,
            PaymentTransactionId = result.TransactionId.ToString("D")
        };

        private static void VerifyCompleted(TestContext context)
        {
            context.Sagas.Verify(service => service.CompleteResultAsync(
                    context.Result.SagaId,
                    context.Result.TransactionId.ToString("D"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        private sealed class TestContext
        {
            public TestContext(
                PaymentResultProcessor processor,
                Mock<ISagaStateService> sagas,
                Mock<IBookingService> bookings,
                SagaState saga,
                PaymentResultV1 result,
                List<BookingNotification> published)
            {
                Processor = processor;
                Sagas = sagas;
                Bookings = bookings;
                Saga = saga;
                Result = result;
                Published = published;
            }

            public PaymentResultProcessor Processor { get; }
            public Mock<ISagaStateService> Sagas { get; }
            public Mock<IBookingService> Bookings { get; }
            public SagaState Saga { get; }
            public PaymentResultV1 Result { get; set; }
            public List<BookingNotification> Published { get; }
        }
    }
}
