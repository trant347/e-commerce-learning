using Confluent.Kafka;

namespace calendar_service.MessageQueue
{
    /// <summary>
    /// Publishes permanently invalid payment messages to their source topic's dead-letter topic.
    /// </summary>
    public interface IKafkaDeadLetterProducer
    {
        Task PublishAsync(
            ConsumeResult<string, string> consumeResult,
            Exception exception,
            int attemptCount,
            CancellationToken cancellationToken);
    }
}
