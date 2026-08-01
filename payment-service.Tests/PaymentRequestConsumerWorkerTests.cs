using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using payment_service.MessageQueue;
using payment_service.Services;
using Payment.Contracts;
using Payment.Contracts.V1;
using Xunit;

namespace payment_service.Tests
{
    public class PaymentRequestConsumerWorkerTests
    {
        [Fact]
        public async Task ProcessConsumeResultAsync_Success_CommitsOffset()
        {
            var processor = new Mock<IPaymentRequestProcessor>();
            var worker = BuildWorker(processor);
            var request = FundingRequest();
            var consumeResult = ConsumeResultFor(request);
            var result = ApprovedResult(request);
            processor.Setup(service => service.ProcessAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
            var consumer = Consumer();

            var processed = await worker.ProcessConsumeResultAsync(
                consumer.Object,
                consumeResult,
                CancellationToken.None);

            Assert.Equal(result, processed);
            consumer.Verify(
                kafka => kafka.Commit(consumeResult),
                Times.Once);
        }

        [Fact]
        public async Task ProcessConsumeResultAsync_ProcessorFailure_DoesNotCommit()
        {
            var processor = new Mock<IPaymentRequestProcessor>();
            var worker = BuildWorker(processor);
            var request = FundingRequest();
            var consumeResult = ConsumeResultFor(request);
            processor.Setup(service => service.ProcessAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("invalid escrow"));
            var consumer = Consumer();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => worker.ProcessConsumeResultAsync(
                    consumer.Object,
                    consumeResult,
                    CancellationToken.None));

            consumer.Verify(
                kafka => kafka.Commit(It.IsAny<ConsumeResult<string, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessConsumeResultAsync_MalformedJson_DoesNotCommit()
        {
            var processor = new Mock<IPaymentRequestProcessor>();
            var worker = BuildWorker(processor);
            var consumeResult = new ConsumeResult<string, string>
            {
                Topic = "payment-requests",
                Partition = new Partition(0),
                Offset = new Offset(1),
                Message = new Message<string, string>
                {
                    Key = Guid.NewGuid().ToString("D"),
                    Value = "{not-json"
                }
            };
            var consumer = Consumer();

            await Assert.ThrowsAsync<JsonException>(
                () => worker.ProcessConsumeResultAsync(
                    consumer.Object,
                    consumeResult,
                    CancellationToken.None));

            processor.Verify(
                service => service.ProcessAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            consumer.Verify(
                kafka => kafka.Commit(It.IsAny<ConsumeResult<string, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessConsumeResultAsync_UnsupportedVersion_DoesNotCommit()
        {
            var processor = new Mock<IPaymentRequestProcessor>();
            var worker = BuildWorker(processor);
            var request = FundingRequest() with { SchemaVersion = 99 };
            var consumeResult = ConsumeResultFor(request);
            processor.Setup(service => service.ProcessAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentException(
                    "Unsupported payment request schema version 99."));
            var consumer = Consumer();

            await Assert.ThrowsAsync<ArgumentException>(
                () => worker.ProcessConsumeResultAsync(
                    consumer.Object,
                    consumeResult,
                    CancellationToken.None));

            consumer.Verify(
                kafka => kafka.Commit(It.IsAny<ConsumeResult<string, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task RewindForRetryAsync_SeeksBackToFailedOffset()
        {
            var processor = new Mock<IPaymentRequestProcessor>();
            var worker = BuildWorker(processor, failureRetryDelaySeconds: 1);
            var consumeResult = ConsumeResultFor(FundingRequest());
            var consumer = Consumer();
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));

            await worker.RewindForRetryAsync(
                consumer.Object,
                consumeResult,
                cancellation.Token);

            consumer.Verify(
                kafka => kafka.Seek(consumeResult.TopicPartitionOffset),
                Times.Once);
        }

        [Fact]
        public async Task HandleFailureAsync_PermanentlyInvalidRequest_DeadLettersAndCommits()
        {
            var processor = new Mock<IPaymentRequestProcessor>();
            var deadLetter = new Mock<IKafkaDeadLetterProducer>();
            deadLetter.Setup(producer => producer.PublishAsync(
                    It.IsAny<ConsumeResult<string, string>>(),
                    It.IsAny<Exception>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var worker = BuildWorker(
                processor,
                deadLetterProducer: deadLetter,
                maxInvalidMessageAttempts: 1);
            var consumeResult = ConsumeResultFor(FundingRequest());
            var consumer = Consumer();
            var exception = new ArgumentException("unsupported schema");

            await worker.HandleFailureAsync(
                consumer.Object,
                consumeResult,
                exception,
                CancellationToken.None);

            deadLetter.Verify(producer => producer.PublishAsync(
                    consumeResult,
                    exception,
                    1,
                    It.IsAny<CancellationToken>()),
                Times.Once);
            consumer.Verify(kafka => kafka.Commit(consumeResult), Times.Once);
            consumer.Verify(
                kafka => kafka.Seek(It.IsAny<TopicPartitionOffset>()),
                Times.Never);
        }

        [Fact]
        public async Task HandleFailureAsync_RetryableWalletRace_RewindsWithoutDeadLetter()
        {
            var processor = new Mock<IPaymentRequestProcessor>();
            var deadLetter = new Mock<IKafkaDeadLetterProducer>();
            var worker = BuildWorker(
                processor,
                failureRetryDelaySeconds: 1,
                deadLetterProducer: deadLetter,
                maxInvalidMessageAttempts: 1);
            var consumeResult = ConsumeResultFor(FundingRequest());
            var consumer = Consumer();
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));

            await worker.HandleFailureAsync(
                consumer.Object,
                consumeResult,
                new PaymentRequestRetryableException(
                    "wallet is not available yet"),
                cancellation.Token);

            consumer.Verify(
                kafka => kafka.Seek(consumeResult.TopicPartitionOffset),
                Times.Once);
            deadLetter.VerifyNoOtherCalls();
            consumer.Verify(
                kafka => kafka.Commit(
                    It.IsAny<ConsumeResult<string, string>>()),
                Times.Never);
        }

        private static PaymentRequestConsumerWorker BuildWorker(
            Mock<IPaymentRequestProcessor> processor,
            int failureRetryDelaySeconds = 5,
            Mock<IKafkaDeadLetterProducer>? deadLetterProducer = null,
            int maxInvalidMessageAttempts = 3)
        {
            var services = new ServiceCollection();
            services.AddSingleton(processor.Object);
            var provider = services.BuildServiceProvider();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KafkaConsumerConfig:BootstrapServers"] = "localhost:9092",
                    ["KafkaConsumerConfig:PaymentRequestTopic"] = "payment-requests",
                    ["KafkaConsumerConfig:PaymentRequestGroupId"] =
                        "payment-service-payment-requests-tests",
                    ["KafkaConsumerConfig:PaymentRequestFailureRetryDelaySeconds"] =
                        failureRetryDelaySeconds.ToString(),
                    ["KafkaConsumerConfig:PaymentRequestMaxInvalidMessageAttempts"] =
                        maxInvalidMessageAttempts.ToString()
                })
                .Build();
            deadLetterProducer ??= new Mock<IKafkaDeadLetterProducer>();
            return new PaymentRequestConsumerWorker(
                NullLogger<PaymentRequestConsumerWorker>.Instance,
                provider,
                deadLetterProducer.Object,
                configuration);
        }

        private static Mock<IConsumer<string, string>> Consumer()
        {
            var consumer = new Mock<IConsumer<string, string>>();
            consumer.Setup(kafka => kafka.Commit(
                It.IsAny<ConsumeResult<string, string>>()));
            return consumer;
        }

        private static ConsumeResult<string, string> ConsumeResultFor(
            PaymentRequestedV1 request) => new()
        {
            Topic = "payment-requests",
            Partition = new Partition(0),
            Offset = new Offset(1),
            Message = new Message<string, string>
            {
                Key = request.KafkaMessageKey,
                Value = JsonSerializer.Serialize(
                    request,
                    PaymentContractJson.SerializerOptions)
            }
        };

        private static PaymentRequestedV1 FundingRequest() => new()
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

        private static PaymentResultV1 ApprovedResult(
            PaymentRequestedV1 request) => new()
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
    }
}
