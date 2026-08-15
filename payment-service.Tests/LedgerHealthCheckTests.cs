using Microsoft.Extensions.Diagnostics.HealthChecks;
using payment_service.Observability;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class LedgerHealthCheckTests
    {
        [Fact]
        public async Task CheckHealthAsync_NoReconciliation_IsDegraded()
        {
            var check = new LedgerHealthCheck(
                new LedgerReconciliationHealthState());

            var result = await check.CheckHealthAsync(
                new HealthCheckContext());

            Assert.Equal(HealthStatus.Degraded, result.Status);
        }

        [Fact]
        public async Task CheckHealthAsync_Anomaly_IsUnhealthy()
        {
            var state = new LedgerReconciliationHealthState();
            state.Update(
                Result(projectionMismatchCount: 1),
                DateTimeOffset.UtcNow);
            var check = new LedgerHealthCheck(state);

            var result = await check.CheckHealthAsync(
                new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Equal(1, result.Data["ledgerAnomalyCount"]);
        }

        private static CustodyReconciliationResult Result(
            int projectionMismatchCount) => new(
                CustodyBalance: 100m,
                FundedEscrowValue: 100m,
                FundedEscrowCount: 1,
                CachedCustodyBalance: 100m,
                UnbalancedEntryCount: 0,
                ProjectionMismatchCount: projectionMismatchCount,
                ProjectionMismatchValue: projectionMismatchCount,
                MissingApprovedJournalCount: 0,
                DeclinedJournalCount: 0,
                EscrowJournalLinkMismatchCount: 0,
                ConflictingEscrowCompletionCount: 0,
                NegativeBalanceCount: 0,
                ClosedAccountPostingCount: 0,
                AppendOnlyProtectionMissingCount: 0);
    }
}
