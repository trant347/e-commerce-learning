using System.Text.Json;
using calendar_service.MessageQueue;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Payment.Contracts;
using Payment.Contracts.V1;
using Xunit;

namespace calendar_service.Tests
{
    public class PaymentResultConsumerWorkerTests
    {
        [Fact]
        public async Task ProcessConsumeResultAsync_Success_CommitsOffset()
        {
            var processor = new Mock<IPaymentResultProcessor>();
            var worker = BuildWorker(processor);
            var result = ApprovedResult();
            var consumeResult = ConsumeResultFor(result);
            processor.Setup(service => service.ProcessAsync(
                    It.IsAny<PaymentResultV1>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(PaymentResultProcessingOutcome.Applied);
            var consumer = Consumer();

            var outcome = await worker.ProcessConsumeResultAsync(
                consumer.Object,
                consumeResult,
                CancellationToken.None);

            Assert.Equal(PaymentResultProcessingOutcome.Applied, outcome);
            consumer.Verify(kafka => kafka.Commit(consumeResult), Times.Once);
        }

        [Fact]
        public async Task ProcessConsumeResultAsync_RetryableFailure_DoesNotCommit()
        {
            var processor = new Mock<IPaymentResultProcessor>();
            var worker = BuildWorker(processor);
            var consumeResult = ConsumeResultFor(ApprovedResult());
            processor.Setup(service => service.ProcessAsync(
                    It.IsAny<PaymentResultV1>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("booking not ready"));
            var consumer = Consumer();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => worker.ProcessConsumeResultAsync(
                    consumer.Object,
                    consumeResult,
                    CancellationToken.None));

            consumer.Verify(
                kafka => kafka.Commit(
                    It.IsAny<ConsumeResult<string, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task ProcessConsumeResultAsync_WrongKafkaKey_DoesNotCommit()
        {
            var processor = new Mock<IPaymentResultProcessor>();
            var worker = BuildWorker(processor);
            var consumeResult = ConsumeResultFor(ApprovedResult());
            consumeResult.Message.Key = Guid.NewGuid().ToString("D");
            var consumer = Consumer();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => worker.ProcessConsumeResultAsync(
                    consumer.Object,
                    consumeResult,
                    CancellationToken.None));

            processor.VerifyNoOtherCalls();
            consumer.Verify(
                kafka => kafka.Commit(
                    It.IsAny<ConsumeResult<string, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task HandleFailureAsync_PermanentlyInvalidResult_DeadLettersAndCommits()
        {
            var processor = new Mock<IPaymentResultProcessor>();
            var deadLetter = new Mock<IKafkaDeadLetterProducer>();
            deadLetter.Setup(producer => producer.PublishAsync(
                    It.IsAny<ConsumeResult<string, string>>(),
                    It.IsAny<Exception>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var worker = BuildWorker(
                processor,
                deadLetter,
                maxInvalidMessageAttempts: 1);
            var consumeResult = ConsumeResultFor(ApprovedResult());
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
        public async Task HandleFailureAsync_InvalidResultBeforeLimit_RewindsWithoutDeadLetter()
        {
            var processor = new Mock<IPaymentResultProcessor>();
            var deadLetter = new Mock<IKafkaDeadLetterProducer>();
            var worker = BuildWorker(
                processor,
                deadLetter,
                maxInvalidMessageAttempts: 2);
            var consumeResult = ConsumeResultFor(ApprovedResult());
            var consumer = Consumer();
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(2));

            await worker.HandleFailureAsync(
                consumer.Object,
                consumeResult,
                new ArgumentException("unsupported schema"),
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

        private static PaymentResultConsumerWorker BuildWorker(
            Mock<IPaymentResultProcessor> processor,
            Mock<IKafkaDeadLetterProducer>? deadLetter = null,
            int maxInvalidMessageAttempts = 3)
        {
            var services = new ServiceCollection();
            services.AddSingleton(processor.Object);
            var provider = services.BuildServiceProvider();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KafkaConsumerConfig:BootstrapServers"] = "localhost:9092",
                    ["KafkaConsumerConfig:PaymentResultTopic"] = "payment-results",
                    ["KafkaConsumerConfig:PaymentResultGroupId"] =
                        "calendar-service-payment-results-tests",
                    ["KafkaConsumerConfig:PaymentResultFailureRetryDelaySeconds"] = "1"
                    ,
                    ["KafkaConsumerConfig:PaymentResultMaxInvalidMessageAttempts"] =
                        maxInvalidMessageAttempts.ToString()
                })
                .Build();
            deadLetter ??= new Mock<IKafkaDeadLetterProducer>();
            return new PaymentResultConsumerWorker(
                NullLogger<PaymentResultConsumerWorker>.Instance,
                provider,
                deadLetter.Object,
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
            PaymentResultV1 result) => new()
        {
            Topic = "payment-results",
            Partition = new Partition(0),
            Offset = new Offset(1),
            Message = new Message<string, string>
            {
                Key = result.KafkaMessageKey,
                Value = JsonSerializer.Serialize(
                    result,
                    PaymentContractJson.SerializerOptions)
            }
        };

        private static PaymentResultV1 ApprovedResult() => new()
        {
            SagaId = Guid.NewGuid(),
            EscrowId = Guid.NewGuid(),
            BookingId = "booking-1",
            Operation = PaymentOperation.FundEscrow,
            TransactionId = Guid.NewGuid(),
            Amount = 100m,
            Currency = "USD",
            Status = PaymentResultV1.StatusApproved
        };
    }
}
