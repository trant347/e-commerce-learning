using System.Text;
using System.Text.Json;
using calendar_service.MessageQueue;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Payment.Contracts.V1;
using Xunit;

namespace calendar_service.Tests
{
    public class PaymentRequestProducerTests
    {
        [Fact]
        public async Task PublishAsync_UsesSagaKeyCamelCaseJsonAndTraceParent()
        {
            var kafka = new Mock<IProducer<string, string>>();
            Message<string, string>? published = null;
            kafka.Setup(p => p.ProduceAsync(
                    "payment-requests",
                    It.IsAny<Message<string, string>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, Message<string, string>, CancellationToken>(
                    (_, message, _) => published = message)
                .ReturnsAsync(new DeliveryResult<string, string>
                {
                    Status = PersistenceStatus.Persisted
                });
            var producer = new PaymentRequestProducer(
                kafka.Object,
                "payment-requests",
                NullLogger<PaymentRequestProducer>.Instance);
            var request = NewRequest();

            await producer.PublishAsync(
                request,
                "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                CancellationToken.None);

            Assert.NotNull(published);
            Assert.Equal(request.SagaId.ToString("D"), published!.Key);
            using var json = JsonDocument.Parse(published.Value);
            Assert.Equal(
                request.SagaId,
                json.RootElement.GetProperty("sagaId").GetGuid());
            Assert.Equal(
                PaymentOperation.FundEscrow,
                json.RootElement.GetProperty("operation").GetString());
            Assert.False(json.RootElement.TryGetProperty("SagaId", out _));
            Assert.Equal(
                "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                Encoding.UTF8.GetString(
                    published.Headers.GetLastBytes("traceparent")));
        }

        [Fact]
        public async Task PublishAsync_NotPersisted_Throws()
        {
            var kafka = new Mock<IProducer<string, string>>();
            kafka.Setup(p => p.ProduceAsync(
                    It.IsAny<string>(),
                    It.IsAny<Message<string, string>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeliveryResult<string, string>
                {
                    Status = PersistenceStatus.NotPersisted
                });
            var producer = new PaymentRequestProducer(
                kafka.Object,
                "payment-requests",
                NullLogger<PaymentRequestProducer>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => producer.PublishAsync(
                    NewRequest(),
                    null,
                    CancellationToken.None));
        }

        private static PaymentRequestedV1 NewRequest() => new()
        {
            SagaId = Guid.NewGuid(),
            EscrowId = Guid.NewGuid(),
            BookingId = "booking-1",
            Operation = PaymentOperation.FundEscrow,
            Amount = 100m,
            Currency = "USD",
            PayerUserId = "requester",
            PayeeUserId = "admin-custody",
            TaskMasterUserId = "taskmaster",
            PaymentMethodToken = "pmt_token"
        };
    }
}
