using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Payment.Contracts.V1;
using Xunit;

namespace calendar_service.Tests
{
    public class PaymentRequestOutboxWorkerTests
    {
        [Fact]
        public async Task RunOnceAsync_SuccessfulPublish_MarksRequestDispatched()
        {
            var (worker, sagaState, producer, saga) = BuildWorker();
            var claimTimestamp = saga.DispatchClaimedAt!.Value;
            sagaState.SetupSequence(s => s.TryClaimNextDispatchAsync(
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(saga)
                .ReturnsAsync((SagaState?)null);
            producer.Setup(p => p.PublishAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            sagaState.Setup(s => s.MarkDispatchedAsync(
                    saga.SagaId,
                    claimTimestamp,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            await worker.RunOnceAsync(CancellationToken.None);

            producer.Verify(p => p.PublishAsync(
                    It.Is<PaymentRequestedV1>(request =>
                        request.SagaId == saga.SagaId),
                    saga.TraceParent,
                    It.IsAny<CancellationToken>()),
                Times.Once);
            sagaState.Verify(s => s.MarkDispatchedAsync(
                    saga.SagaId,
                    claimTimestamp,
                    It.IsAny<CancellationToken>()),
                Times.Once);
            sagaState.Verify(s => s.RescheduleDispatchAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RunOnceAsync_KafkaFailure_ReschedulesWithBackoff()
        {
            var (worker, sagaState, producer, saga) = BuildWorker();
            saga.DispatchAttemptCount = 3;
            sagaState.SetupSequence(s => s.TryClaimNextDispatchAsync(
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(saga)
                .ReturnsAsync((SagaState?)null);
            producer.Setup(p => p.PublishAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Kafka unavailable"));
            DateTime nextAttemptAt = default;
            sagaState.Setup(s => s.RescheduleDispatchAsync(
                    saga.SagaId,
                    saga.DispatchClaimedAt!.Value,
                    It.IsAny<DateTime>(),
                    "Kafka unavailable",
                    It.IsAny<CancellationToken>()))
                .Callback<Guid, DateTime, DateTime, string, CancellationToken>(
                    (_, _, next, _, _) => nextAttemptAt = next)
                .ReturnsAsync(true);
            var before = DateTime.UtcNow;

            await worker.RunOnceAsync(CancellationToken.None);

            Assert.InRange(
                nextAttemptAt,
                before.AddSeconds(3),
                DateTime.UtcNow.AddSeconds(5));
            sagaState.Verify(s => s.MarkDispatchedAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RunOnceAsync_RetriedClaimPublishesAndAcknowledges()
        {
            var (worker, sagaState, producer, saga) = BuildWorker();
            var claimTimestamp = saga.DispatchClaimedAt!.Value;
            saga.DispatchAttemptCount = 2;
            saga.LastDispatchError = "previous failure";
            sagaState.SetupSequence(s => s.TryClaimNextDispatchAsync(
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(saga)
                .ReturnsAsync((SagaState?)null);
            producer.Setup(p => p.PublishAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            sagaState.Setup(s => s.MarkDispatchedAsync(
                    saga.SagaId,
                    claimTimestamp,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            await worker.RunOnceAsync(CancellationToken.None);

            producer.Verify(p => p.PublishAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            sagaState.Verify(s => s.MarkDispatchedAsync(
                    saga.SagaId,
                    claimTimestamp,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RunOnceAsync_DispatchedRequestIsNotPublishedAgain()
        {
            var (worker, sagaState, producer, saga) = BuildWorker();
            sagaState.SetupSequence(s => s.TryClaimNextDispatchAsync(
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(saga)
                .ReturnsAsync((SagaState?)null)
                .ReturnsAsync((SagaState?)null);
            producer.Setup(p => p.PublishAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            sagaState.Setup(s => s.MarkDispatchedAsync(
                    saga.SagaId,
                    saga.DispatchClaimedAt!.Value,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            await worker.RunOnceAsync(CancellationToken.None);
            await worker.RunOnceAsync(CancellationToken.None);

            producer.Verify(p => p.PublishAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        private static (
            PaymentRequestOutboxWorker Worker,
            Mock<ISagaStateService> SagaState,
            Mock<IPaymentRequestProducer> Producer,
            SagaState Saga) BuildWorker()
        {
            var sagaState = new Mock<ISagaStateService>();
            var producer = new Mock<IPaymentRequestProducer>();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PaymentRequestOutbox:PollIntervalSeconds"] = "1",
                    ["PaymentRequestOutbox:ClaimLeaseSeconds"] = "30",
                    ["PaymentRequestOutbox:BaseRetryDelaySeconds"] = "1",
                    ["PaymentRequestOutbox:MaxRetryDelaySeconds"] = "60",
                    ["PaymentRequestOutbox:MaxBatchSize"] = "10"
                })
                .Build();
            var worker = new PaymentRequestOutboxWorker(
                sagaState.Object,
                producer.Object,
                NullLogger<PaymentRequestOutboxWorker>.Instance,
                configuration);
            return (worker, sagaState, producer, NewClaimedSaga());
        }

        private static SagaState NewClaimedSaga()
        {
            var request = new PaymentRequestedV1
            {
                SagaId = Guid.NewGuid(),
                EscrowId = Guid.NewGuid(),
                BookingId = "booking-1",
                Operation = PaymentOperation.FundEscrow,
                Amount = 100m,
                Currency = "USD",
                PayerUserId = "requester",
                PayeeUserId = "admin-custody",
                PaymentMethodToken = "pmt_token"
            };
            return new SagaState
            {
                SagaId = request.SagaId,
                EscrowId = request.EscrowId,
                BookingId = request.BookingId,
                Operation = request.Operation,
                Status = SagaState.StatusStarted,
                DispatchStatus = SagaDispatchStatus.CLAIMED,
                DispatchAttemptCount = 1,
                DispatchClaimedAt = DateTime.UtcNow,
                DispatchClaimExpiresAt = DateTime.UtcNow.AddSeconds(30),
                TraceParent = "00-trace-parent",
                PaymentRequest = PendingPaymentRequest.FromContract(request)
            };
        }
    }
}
