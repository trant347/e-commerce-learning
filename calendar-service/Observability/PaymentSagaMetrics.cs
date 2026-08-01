using System.Diagnostics.Metrics;

namespace calendar_service.Observability
{
    /// <summary>
    /// Defines calendar-service metrics for saga recovery, outboxes, consumers, and dead letters.
    /// </summary>
    public static class PaymentSagaMetrics
    {
        public const string MeterName = "calendar-service.PaymentSaga";

        private static readonly Meter s_meter = new(MeterName);

        public static readonly Counter<long> OutboxRetries =
            s_meter.CreateCounter<long>("payment_saga.outbox.retries");
        public static readonly Counter<long> DeadLetters =
            s_meter.CreateCounter<long>("payment_saga.dlq.messages");
        public static readonly Histogram<double> ProcessingDuration =
            s_meter.CreateHistogram<double>(
                "payment_saga.processing.duration",
                "ms");
        public static readonly Histogram<double> PendingSagaAge =
            s_meter.CreateHistogram<double>(
                "payment_saga.pending.age",
                "s");
        public static readonly Histogram<long> ConsumerLag =
            s_meter.CreateHistogram<long>("payment_saga.consumer.lag");
        public static readonly Histogram<long> OutboxBacklog =
            s_meter.CreateHistogram<long>("payment_saga.outbox.backlog");
    }
}
