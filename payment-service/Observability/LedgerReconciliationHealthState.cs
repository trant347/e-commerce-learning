using payment_service.Services;

namespace payment_service.Observability
{
    public sealed record LedgerReconciliationHealthSnapshot(
        CustodyReconciliationResult? Result,
        DateTimeOffset? CheckedAt);

    public sealed class LedgerReconciliationHealthState
    {
        private LedgerReconciliationHealthSnapshot _snapshot =
            new(null, null);

        public LedgerReconciliationHealthSnapshot Snapshot =>
            Volatile.Read(ref _snapshot);

        public void Update(
            CustodyReconciliationResult result,
            DateTimeOffset checkedAt)
        {
            ArgumentNullException.ThrowIfNull(result);
            Volatile.Write(
                ref _snapshot,
                new LedgerReconciliationHealthSnapshot(result, checkedAt));
        }
    }
}
