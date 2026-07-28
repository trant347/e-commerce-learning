using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Payment.Contracts;
using Payment.Contracts.V1;
using payment_service.MessageQueue;
using payment_service.Models;
using Xunit;

namespace payment_service.Tests
{
    public class PaymentResultOutboxWorkerTests
    {
        private static readonly DateTimeOffset Now =
            new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task RunOnceAsync_PersistedKafkaAck_MarksResultDispatched()
        {
            var row = ClaimedRow();
            var store = new Mock<IPaymentResultOutboxStore>();
            store.Setup(service => service.ReconcileMissingAsync(
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);
            store.SetupSequence(service => service.TryClaimNextAsync(
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(row)
                .ReturnsAsync((PaymentResultOutbox?)null);
            store.Setup(service => service.MarkDispatchedAsync(
                    row.Id,
                    row.DispatchClaimedAt!.Value,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var producer = new Mock<IPaymentResultProducer>();
            producer.Setup(service => service.PublishAsync(
                    It.IsAny<PaymentResultV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var worker = BuildWorker(store, producer);
            var claimTimestamp = row.DispatchClaimedAt!.Value;

            await worker.RunOnceAsync(CancellationToken.None);

            producer.Verify(service => service.PublishAsync(
                    It.Is<PaymentResultV1>(result =>
                        result.SagaId == row.SagaId
                        && result.TransactionId == row.TransactionId),
                    row.TraceParent,
                    It.IsAny<CancellationToken>()),
                Times.Once);
            store.Verify(service => service.MarkDispatchedAsync(
                    row.Id,
                    claimTimestamp,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RunOnceAsync_KafkaFailure_ReschedulesWithBackoff()
        {
            var row = ClaimedRow();
            row.DispatchAttemptCount = 3;
            var store = new Mock<IPaymentResultOutboxStore>();
            store.Setup(service => service.ReconcileMissingAsync(
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);
            store.SetupSequence(service => service.TryClaimNextAsync(
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(row)
                .ReturnsAsync((PaymentResultOutbox?)null);
            DateTime nextAttemptAt = default;
            store.Setup(service => service.RescheduleAsync(
                    row.Id,
                    row.DispatchClaimedAt!.Value,
                    It.IsAny<DateTime>(),
                    "Kafka unavailable",
                    It.IsAny<CancellationToken>()))
                .Callback<Guid, DateTime, DateTime, string, CancellationToken>(
                    (_, _, next, _, _) => nextAttemptAt = next)
                .ReturnsAsync(true);
            var producer = new Mock<IPaymentResultProducer>();
            producer.Setup(service => service.PublishAsync(
                    It.IsAny<PaymentResultV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(
                    new InvalidOperationException("Kafka unavailable"));
            var worker = BuildWorker(store, producer);

            await worker.RunOnceAsync(CancellationToken.None);

            Assert.Equal(Now.UtcDateTime.AddSeconds(4), nextAttemptAt);
            store.Verify(service => service.MarkDispatchedAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task UnpublishedResult_AfterWorkerRestart_IsRepublished()
        {
            var firstClaim = ClaimedRow();
            var retryClaim = ClaimedRow(
                firstClaim.Id,
                firstClaim.SagaId,
                firstClaim.TransactionId);
            retryClaim.DispatchAttemptCount = 2;
            retryClaim.DispatchClaimedAt = Now.UtcDateTime.AddMinutes(1);
            var store = new Mock<IPaymentResultOutboxStore>();
            store.Setup(service => service.ReconcileMissingAsync(
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(0);
            store.SetupSequence(service => service.TryClaimNextAsync(
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstClaim)
                .ReturnsAsync((PaymentResultOutbox?)null)
                .ReturnsAsync(retryClaim)
                .ReturnsAsync((PaymentResultOutbox?)null);
            store.Setup(service => service.RescheduleAsync(
                    firstClaim.Id,
                    firstClaim.DispatchClaimedAt!.Value,
                    It.IsAny<DateTime>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            store.Setup(service => service.MarkDispatchedAsync(
                    retryClaim.Id,
                    retryClaim.DispatchClaimedAt!.Value,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var producer = new Mock<IPaymentResultProducer>();
            producer.SetupSequence(service => service.PublishAsync(
                    It.IsAny<PaymentResultV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("process stopped"))
                .Returns(Task.CompletedTask);

            await BuildWorker(store, producer)
                .RunOnceAsync(CancellationToken.None);
            await BuildWorker(store, producer)
                .RunOnceAsync(CancellationToken.None);

            producer.Verify(service => service.PublishAsync(
                    It.IsAny<PaymentResultV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            store.Verify(service => service.MarkDispatchedAsync(
                    retryClaim.Id,
                    retryClaim.DispatchClaimedAt.Value,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        private static PaymentResultOutboxWorker BuildWorker(
            Mock<IPaymentResultOutboxStore> store,
            Mock<IPaymentResultProducer> producer)
        {
            var services = new ServiceCollection();
            services.AddSingleton(store.Object);
            var provider = services.BuildServiceProvider();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PaymentResultOutbox:PollIntervalSeconds"] = "1",
                    ["PaymentResultOutbox:ClaimLeaseSeconds"] = "60",
                    ["PaymentResultProducer:MessageTimeoutMs"] = "30000",
                    ["PaymentResultOutbox:BaseRetryDelaySeconds"] = "1",
                    ["PaymentResultOutbox:MaxRetryDelaySeconds"] = "60",
                    ["PaymentResultOutbox:MaxBatchSize"] = "10"
                })
                .Build();
            return new PaymentResultOutboxWorker(
                provider,
                producer.Object,
                new FixedTimeProvider(Now),
                NullLogger<PaymentResultOutboxWorker>.Instance,
                configuration);
        }

        private static PaymentResultOutbox ClaimedRow(
            Guid? id = null,
            Guid? sagaId = null,
            Guid? transactionId = null)
        {
            var result = new PaymentResultV1
            {
                SagaId = sagaId ?? Guid.NewGuid(),
                EscrowId = Guid.NewGuid(),
                BookingId = "booking-1",
                Operation = PaymentOperation.FundEscrow,
                TransactionId = transactionId ?? Guid.NewGuid(),
                Amount = 100m,
                Currency = "USD",
                Status = PaymentResultV1.StatusApproved
            };
            return new PaymentResultOutbox
            {
                Id = id ?? Guid.NewGuid(),
                SagaId = result.SagaId,
                TransactionId = result.TransactionId,
                Payload = JsonSerializer.Serialize(
                    result,
                    PaymentContractJson.SerializerOptions),
                DispatchStatus = PaymentResultOutbox.StatusClaimed,
                DispatchAttemptCount = 1,
                DispatchClaimedAt = Now.UtcDateTime,
                DispatchClaimExpiresAt =
                    Now.UtcDateTime.AddSeconds(30),
                NextDispatchAttemptAt = Now.UtcDateTime,
                TraceParent = "00-trace-parent",
                CreatedAt = Now.UtcDateTime
            };
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _utcNow;

            public FixedTimeProvider(DateTimeOffset utcNow)
            {
                _utcNow = utcNow;
            }

            public override DateTimeOffset GetUtcNow() => _utcNow;
        }
    }
}
