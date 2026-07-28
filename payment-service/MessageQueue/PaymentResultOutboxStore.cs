using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using payment_service.Data;
using payment_service.Models;
using Payment.Contracts;
using Payment.Contracts.V1;

namespace payment_service.MessageQueue
{
    public sealed class PaymentResultOutboxStore : IPaymentResultOutboxStore
    {
        private readonly PaymentDbContext _dbContext;
        private readonly TimeProvider _timeProvider;

        public PaymentResultOutboxStore(
            PaymentDbContext dbContext,
            TimeProvider timeProvider)
        {
            _dbContext = dbContext;
            _timeProvider = timeProvider;
        }

        public async Task<int> ReconcileMissingAsync(
            CancellationToken cancellationToken)
        {
            if (UsesPostgres())
            {
                return await _dbContext.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO payment_result_outbox (
                        "Id",
                        "SagaId",
                        "TransactionId",
                        "Payload",
                        "DispatchStatus",
                        "DispatchAttemptCount",
                        "NextDispatchAttemptAt",
                        "CreatedAt")
                    SELECT
                        "Id",
                        "SagaId",
                        "Id",
                        jsonb_strip_nulls(jsonb_build_object(
                            'schemaVersion', 1,
                            'sagaId', "SagaId",
                            'escrowId', "EscrowId",
                            'bookingId', "BookingId",
                            'operation', "Operation",
                            'transactionId', "Id",
                            'amount', "Amount",
                            'currency', "Currency",
                            'status', "Status",
                            'declineReason', "DeclineReason")),
                        'PENDING',
                        0,
                        NOW(),
                        "CreatedAt"
                    FROM payment_transactions AS source_transaction
                    WHERE source_transaction."SagaId" IS NOT NULL
                      AND source_transaction."EscrowId" IS NOT NULL
                      AND source_transaction."BookingId" IS NOT NULL
                      AND source_transaction."Operation" IS NOT NULL
                      AND NOT EXISTS (
                        SELECT 1
                        FROM payment_result_outbox AS outbox
                        WHERE outbox."SagaId" = source_transaction."SagaId"
                      )
                    ON CONFLICT DO NOTHING;
                    """,
                    cancellationToken);
            }

            var missing = await _dbContext.Transactions
                .Where(transaction =>
                    transaction.SagaId != null
                    && transaction.EscrowId != null
                    && transaction.BookingId != null
                    && transaction.Operation != null
                    && !_dbContext.PaymentResultOutbox.Any(row =>
                        row.SagaId == transaction.SagaId))
                .ToListAsync(cancellationToken);
            var now = UtcNow();
            foreach (var transaction in missing)
            {
                var result = ToResult(transaction);
                _dbContext.PaymentResultOutbox.Add(new PaymentResultOutbox
                {
                    Id = transaction.Id,
                    SagaId = result.SagaId,
                    TransactionId = result.TransactionId,
                    Payload = JsonSerializer.Serialize(
                        result,
                        PaymentContractJson.SerializerOptions),
                    NextDispatchAttemptAt = now,
                    CreatedAt = transaction.CreatedAt
                });
            }

            if (missing.Count > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return missing.Count;
        }

        public async Task<PaymentResultOutbox?> TryClaimNextAsync(
            TimeSpan claimLease,
            CancellationToken cancellationToken)
        {
            var now = UtcNow();
            if (!UsesPostgres())
            {
                var candidate = await _dbContext.PaymentResultOutbox
                    .Where(row =>
                        row.NextDispatchAttemptAt <= now
                        && (row.DispatchStatus == PaymentResultOutbox.StatusPending
                            || (row.DispatchStatus == PaymentResultOutbox.StatusClaimed
                                && row.DispatchClaimExpiresAt <= now)))
                    .OrderBy(row => row.NextDispatchAttemptAt)
                    .ThenBy(row => row.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                if (candidate == null)
                {
                    return null;
                }

                Claim(candidate, now, claimLease);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return candidate;
            }

            await using var transaction =
                await _dbContext.Database.BeginTransactionAsync(
                    cancellationToken);
            var row = await _dbContext.PaymentResultOutbox
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM payment_result_outbox
                    WHERE "NextDispatchAttemptAt" <= {now}
                      AND (
                        "DispatchStatus" = {PaymentResultOutbox.StatusPending}
                        OR (
                          "DispatchStatus" = {PaymentResultOutbox.StatusClaimed}
                          AND "DispatchClaimExpiresAt" <= {now}
                        )
                      )
                    ORDER BY "NextDispatchAttemptAt", "CreatedAt"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                    """)
                .SingleOrDefaultAsync(cancellationToken);
            if (row == null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            Claim(row, now, claimLease);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return row;
        }

        public async Task<bool> MarkDispatchedAsync(
            Guid outboxId,
            DateTime claimTimestamp,
            CancellationToken cancellationToken)
        {
            var now = UtcNow();
            if (_dbContext.Database.IsRelational())
            {
                return await _dbContext.PaymentResultOutbox
                    .Where(row =>
                        row.Id == outboxId
                        && row.DispatchStatus
                            == PaymentResultOutbox.StatusClaimed
                        && row.DispatchClaimedAt == claimTimestamp)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                row => row.DispatchStatus,
                                PaymentResultOutbox.StatusDispatched)
                            .SetProperty(row => row.DispatchedAt, now)
                            .SetProperty(
                                row => row.DispatchClaimedAt,
                                (DateTime?)null)
                            .SetProperty(
                                row => row.DispatchClaimExpiresAt,
                                (DateTime?)null)
                            .SetProperty(
                                row => row.LastDispatchError,
                                (string?)null),
                        cancellationToken) == 1;
            }

            var row = await CurrentClaimAsync(
                outboxId,
                claimTimestamp,
                cancellationToken);
            if (row == null)
            {
                return false;
            }
            row.DispatchStatus = PaymentResultOutbox.StatusDispatched;
            row.DispatchedAt = now;
            row.DispatchClaimedAt = null;
            row.DispatchClaimExpiresAt = null;
            row.LastDispatchError = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<bool> RescheduleAsync(
            Guid outboxId,
            DateTime claimTimestamp,
            DateTime nextAttemptAt,
            string error,
            CancellationToken cancellationToken)
        {
            var truncatedError = error.Length <= 1000
                ? error
                : error[..1000];
            if (_dbContext.Database.IsRelational())
            {
                return await _dbContext.PaymentResultOutbox
                    .Where(row =>
                        row.Id == outboxId
                        && row.DispatchStatus
                            == PaymentResultOutbox.StatusClaimed
                        && row.DispatchClaimedAt == claimTimestamp)
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(
                                row => row.DispatchStatus,
                                PaymentResultOutbox.StatusPending)
                            .SetProperty(
                                row => row.NextDispatchAttemptAt,
                                nextAttemptAt)
                            .SetProperty(
                                row => row.DispatchClaimedAt,
                                (DateTime?)null)
                            .SetProperty(
                                row => row.DispatchClaimExpiresAt,
                                (DateTime?)null)
                            .SetProperty(
                                row => row.LastDispatchError,
                                truncatedError),
                        cancellationToken) == 1;
            }

            var row = await CurrentClaimAsync(
                outboxId,
                claimTimestamp,
                cancellationToken);
            if (row == null)
            {
                return false;
            }
            row.DispatchStatus = PaymentResultOutbox.StatusPending;
            row.NextDispatchAttemptAt = nextAttemptAt;
            row.DispatchClaimedAt = null;
            row.DispatchClaimExpiresAt = null;
            row.LastDispatchError = truncatedError;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        private Task<PaymentResultOutbox?> CurrentClaimAsync(
            Guid outboxId,
            DateTime claimTimestamp,
            CancellationToken cancellationToken) =>
            _dbContext.PaymentResultOutbox.SingleOrDefaultAsync(
                row =>
                    row.Id == outboxId
                    && row.DispatchStatus == PaymentResultOutbox.StatusClaimed
                    && row.DispatchClaimedAt == claimTimestamp,
                cancellationToken);

        private static void Claim(
            PaymentResultOutbox row,
            DateTime now,
            TimeSpan claimLease)
        {
            row.DispatchStatus = PaymentResultOutbox.StatusClaimed;
            row.DispatchAttemptCount++;
            row.DispatchClaimedAt = now;
            row.DispatchClaimExpiresAt = now.Add(claimLease);
        }

        private DateTime UtcNow() =>
            _timeProvider.GetUtcNow().UtcDateTime;

        private bool UsesPostgres() =>
            _dbContext.Database.ProviderName
                == "Npgsql.EntityFrameworkCore.PostgreSQL";

        private static PaymentResultV1 ToResult(
            PaymentTransaction transaction) => new()
        {
            SagaId = transaction.SagaId!.Value,
            EscrowId = transaction.EscrowId!.Value,
            BookingId = transaction.BookingId!,
            Operation = transaction.Operation!,
            TransactionId = transaction.Id,
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            Status = transaction.Status,
            DeclineReason = transaction.DeclineReason
        };
    }
}
