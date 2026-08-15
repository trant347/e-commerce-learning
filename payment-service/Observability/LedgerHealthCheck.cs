using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace payment_service.Observability
{
    public sealed class LedgerHealthCheck : IHealthCheck
    {
        private readonly LedgerReconciliationHealthState _state;

        public LedgerHealthCheck(LedgerReconciliationHealthState state)
        {
            _state = state;
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            var snapshot = _state.Snapshot;
            if (snapshot.Result == null)
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    "Ledger reconciliation has not completed yet."));
            }

            var data = new Dictionary<string, object>
            {
                ["checkedAt"] = snapshot.CheckedAt?.ToString("O") ?? string.Empty,
                ["custodyDifference"] = snapshot.Result.Difference,
                ["ledgerAnomalyCount"] = snapshot.Result.LedgerAnomalyCount,
                ["projectionMismatchValue"] =
                    snapshot.Result.ProjectionMismatchValue
            };
            return Task.FromResult(snapshot.Result.IsHealthy
                ? HealthCheckResult.Healthy(
                    "Ledger reconciliation is healthy.",
                    data)
                : HealthCheckResult.Unhealthy(
                    "Ledger reconciliation detected financial invariant failures.",
                    data: data));
        }
    }
}
