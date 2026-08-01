using System.Diagnostics.Metrics;

namespace payment_service.Observability
{
    /// <summary>
    /// Defines payment-service metrics for saga processing, outboxes, escrows, and custody health.
    /// </summary>
    public static class PaymentSagaMetrics
    {
        public const string MeterName = "payment-service.PaymentSaga";

        private static readonly Meter s_meter = new(MeterName);

        public static readonly Counter<long> OutboxRetries =
            s_meter.CreateCounter<long>("payment_saga.outbox.retries");
        public static readonly Counter<long> DeadLetters =
            s_meter.CreateCounter<long>("payment_saga.dlq.messages");
        public static readonly Histogram<double> ProcessingDuration =
            s_meter.CreateHistogram<double>(
                "payment_saga.processing.duration",
                "ms");
        public static readonly Histogram<long> ConsumerLag =
            s_meter.CreateHistogram<long>("payment_saga.consumer.lag");
        public static readonly Histogram<long> OutboxBacklog =
            s_meter.CreateHistogram<long>("payment_saga.outbox.backlog");
        public static readonly Histogram<double> EscrowAge =
            s_meter.CreateHistogram<double>("payment_saga.escrow.age", "s");
        public static readonly Histogram<double> EscrowValue =
            s_meter.CreateHistogram<double>("payment_saga.escrow.value", "USD");
        public static readonly Histogram<double> CustodyMismatch =
            s_meter.CreateHistogram<double>("payment_saga.custody.mismatch", "USD");
    }
}
