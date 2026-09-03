using calendar_service.Model;
using calendar_service.Services;
using calendar_service.Services.Contracts;
using calendar_service.Services.Implementation;
using Microsoft.Extensions.Configuration;
using Moq;
using Payment.Contracts.V1;
using Xunit;

namespace calendar_service.Tests
{
    /// <summary>
    /// Escrow orchestration moved out of BookingController so REST and future MCP callers share
    /// one path to money movement. These tests cover the parts that are no longer reachable
    /// through an HTTP status assertion: the exact conflict wording the UI shows, and the fact
    /// that the caller never gets to choose the payer, payee or amount.
    /// </summary>
    public class EscrowPaymentServiceTests
    {
        private const string Custody = "admin-custody";
        private const string Requester = "alice";
        private const string TaskMaster = "bob";

        [Theory]
        [InlineData(PaymentOperation.FundEscrow, "Escrow funding for this booking is already being processed")]
        [InlineData(PaymentOperation.ReleaseEscrow, "Escrow release for this booking is already being processed")]
        [InlineData(PaymentOperation.RefundEscrow, "Escrow refund for this booking is already being processed")]
        public async Task EnsureNoActiveOperation_ActiveSaga_ThrowsWithTheUserFacingWording(
            string operation,
            string expectedMessage)
        {
            var saga = new Mock<ISagaStateService>();
            saga.Setup(s => s.GetLatestByBookingIdAsync("bk-1")).ReturnsAsync(new SagaState
            {
                BookingId = "bk-1",
                Operation = operation,
                Status = SagaState.StatusStarted
            });

            var ex = await Assert.ThrowsAsync<ActivePaymentSagaException>(
                () => Build(new Mock<IBookingService>(), saga)
                    .EnsureNoActiveOperationAsync("bk-1", operation));

            Assert.Equal(expectedMessage, ex.Message);
        }

        [Fact]
        public async Task EnsureNoActiveOperation_DifferentOperationInFlight_DoesNotBlock()
        {
            var saga = new Mock<ISagaStateService>();
            saga.Setup(s => s.GetLatestByBookingIdAsync("bk-1")).ReturnsAsync(new SagaState
            {
                BookingId = "bk-1",
                Operation = PaymentOperation.FundEscrow,
                Status = SagaState.StatusStarted
            });

            await Build(new Mock<IBookingService>(), saga)
                .EnsureNoActiveOperationAsync("bk-1", PaymentOperation.RefundEscrow);
        }

        [Fact]
        public async Task FundEscrow_UsesTheServerSideAgreedPrice_NotAnythingFromTheCaller()
        {
            var booking = AcceptedBooking(escrowId: Guid.NewGuid());
            var (service, saga, captured) = Arrange(booking);

            await Build(service, saga).FundEscrowAsync("bk-1", Requester, "  pmt_token  ");

            Assert.Equal(150m, captured.Single().Amount);
            Assert.Equal("USD", captured.Single().Currency);
            Assert.Equal(PaymentOperation.FundEscrow, captured.Single().Operation);
            Assert.Equal(Requester, captured.Single().PayerUserId);
            Assert.Equal(Custody, captured.Single().PayeeUserId);
            Assert.Equal("pmt_token", captured.Single().PaymentMethodToken);
        }

        [Fact]
        public async Task FundEscrow_ReusesTheAttachedEscrowOnRetry()
        {
            var escrowId = Guid.NewGuid();
            var booking = AcceptedBooking(escrowId);
            var (service, saga, captured) = Arrange(booking);

            var accepted = await Build(service, saga)
                .FundEscrowAsync("bk-1", Requester, "pmt_token");

            Assert.Equal(escrowId, accepted.EscrowId);
            Assert.Equal(escrowId, captured.Single().EscrowId);
            service.Verify(
                s => s.AttachEscrowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>()),
                Times.Never);
        }

        [Fact]
        public async Task FundEscrow_UnknownBooking_ThrowsKeyNotFound()
        {
            var service = new Mock<IBookingService>();
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync((Booking?)null);

            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => Build(service, new Mock<ISagaStateService>())
                    .FundEscrowAsync("bk-1", Requester, "pmt_token"));
        }

        [Fact]
        public async Task FundEscrow_NonRequester_ThrowsUnauthorized()
        {
            var (service, saga, _) = Arrange(AcceptedBooking(Guid.NewGuid()));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => Build(service, saga).FundEscrowAsync("bk-1", TaskMaster, "pmt_token"));
        }

        [Fact]
        public async Task FundEscrow_WithoutCustodyConfiguration_ThrowsEscrowConfiguration()
        {
            // Must not surface as a 409: the booking is fine, the deployment is not.
            var (service, saga, _) = Arrange(AcceptedBooking(Guid.NewGuid()));

            await Assert.ThrowsAsync<EscrowConfigurationException>(
                () => Build(service, saga, custodyUserId: null)
                    .FundEscrowAsync("bk-1", Requester, "pmt_token"));
        }

        [Theory]
        [InlineData(PaymentOperation.ReleaseEscrow, TaskMaster)]
        [InlineData(PaymentOperation.RefundEscrow, Requester)]
        public async Task EnqueueTransfer_PaysOutOfCustodyToThePartyTheOperationImplies(
            string operation,
            string expectedPayee)
        {
            var booking = AcceptedBooking(Guid.NewGuid());
            var (service, saga, captured) = Arrange(booking);

            await Build(service, saga).EnqueueTransferAsync(booking, operation);

            Assert.Equal(Custody, captured.Single().PayerUserId);
            Assert.Equal(expectedPayee, captured.Single().PayeeUserId);
            Assert.Equal(150m, captured.Single().Amount);
        }

        [Fact]
        public async Task EnqueueTransfer_RejectsAnOperationThatIsNotATransfer()
        {
            var booking = AcceptedBooking(Guid.NewGuid());
            var (service, saga, _) = Arrange(booking);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => Build(service, saga)
                    .EnqueueTransferAsync(booking, PaymentOperation.FundEscrow));
        }

        [Fact]
        public async Task EnqueueTransfer_WithoutEscrow_Throws()
        {
            var booking = AcceptedBooking(escrowId: null);
            var (service, saga, _) = Arrange(booking);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Build(service, saga)
                    .EnqueueTransferAsync(booking, PaymentOperation.ReleaseEscrow));
        }

        [Fact]
        public async Task AcceptedResponse_PointsAtTheSagaStatusResource()
        {
            var booking = AcceptedBooking(Guid.NewGuid());
            var (service, saga, _) = Arrange(booking);

            var accepted = await Build(service, saga)
                .FundEscrowAsync("bk-1", Requester, "pmt_token");

            Assert.Equal($"/api/booking/payment-status/{accepted.SagaId:D}", accepted.StatusUrl);
        }

        private static (Mock<IBookingService>, Mock<ISagaStateService>, List<PaymentRequestedV1>) Arrange(
            Booking booking)
        {
            var service = new Mock<IBookingService>();
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);

            var saga = new Mock<ISagaStateService>();
            var captured = new List<PaymentRequestedV1>();
            saga.Setup(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<PaymentRequestedV1, string?, CancellationToken>((r, _, _) => captured.Add(r))
                .ReturnsAsync(new SagaState());

            return (service, saga, captured);
        }

        private static EscrowPaymentService Build(
            Mock<IBookingService> service,
            Mock<ISagaStateService> saga,
            string? custodyUserId = Custody) =>
            new(
                service.Object,
                saga.Object,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Escrow:CustodyUserId"] = custodyUserId
                    })
                    .Build());

        private static Booking AcceptedBooking(Guid? escrowId) => new()
        {
            Id = "bk-1",
            TaskMasterId = "tm-1",
            TaskMasterUsername = TaskMaster,
            RequesterUsername = Requester,
            SlotStart = DateTime.UtcNow.AddDays(2),
            DurationHours = 3,
            Status = Booking.StatusAccepted,
            AgreedAmount = 150m,
            AgreedCurrency = "USD",
            EscrowId = escrowId,
            EscrowStatus = escrowId.HasValue ? EscrowStatus.Pending : null
        };
    }
}
