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
        public static readonly Histogram<double> LedgerProjectionMismatch =
            s_meter.CreateHistogram<double>(
                "payment_ledger.projection.mismatch",
                "USD");
        public static readonly Histogram<long> LedgerAnomalies =
            s_meter.CreateHistogram<long>("payment_ledger.anomalies");
        public static readonly Counter<long> LedgerPostings =
            s_meter.CreateCounter<long>("payment_ledger.postings");
        public static readonly Counter<long> LedgerPostingFailures =
            s_meter.CreateCounter<long>("payment_ledger.posting.failures");
        public static readonly Histogram<double> LedgerPostingDuration =
            s_meter.CreateHistogram<double>(
                "payment_ledger.posting.duration",
                "ms");
        public static readonly Histogram<long> UnbalancedJournalEntries =
            s_meter.CreateHistogram<long>(
                "payment_ledger.reconciliation.unbalanced_entries");
        public static readonly Histogram<long> MissingJournalLinks =
            s_meter.CreateHistogram<long>(
                "payment_ledger.reconciliation.missing_links");
        public static readonly Histogram<double> OldestUnreconciledEntryAge =
            s_meter.CreateHistogram<double>(
                "payment_ledger.reconciliation.oldest_unreconciled_age",
                "s");
    }
}
