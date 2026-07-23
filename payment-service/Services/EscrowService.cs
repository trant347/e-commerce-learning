using Microsoft.EntityFrameworkCore;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    /// <summary>
    /// Maintains the authoritative escrow ledger. Transition methods use conditional updates on
    /// relational databases, so competing release/refund attempts cannot both succeed.
    /// They participate in an ambient EF transaction when the future payment consumer combines
    /// the transition with wallet movement and transaction persistence.
    /// </summary>
    public class EscrowService : IEscrowService
    {
        private readonly PaymentDbContext _dbContext;
        private readonly TimeProvider _timeProvider;

        public EscrowService(PaymentDbContext dbContext, TimeProvider timeProvider)
        {
            _dbContext = dbContext;
            _timeProvider = timeProvider;
        }

        public async Task<EscrowRecord> CreateAsync(
            Guid escrowId,
            string bookingId,
            decimal amount,
            string currency,
            string requesterUserId,
            string taskMasterUserId,
            string custodyUserId,
            CancellationToken cancellationToken = default)
        {
            if (escrowId == Guid.Empty)
            {
                throw new ArgumentException("Escrow id is required.", nameof(escrowId));
            }

            bookingId = RequireValue(bookingId, nameof(bookingId));
            requesterUserId = RequireValue(requesterUserId, nameof(requesterUserId));
            taskMasterUserId = RequireValue(taskMasterUserId, nameof(taskMasterUserId));
            custodyUserId = RequireValue(custodyUserId, nameof(custodyUserId));
            currency = RequireValue(currency, nameof(currency)).ToUpperInvariant();

            amount = Math.Round(amount, 2, MidpointRounding.ToEven);
            if (amount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "Escrow amount must be greater than zero.");
            }
            if (currency.Length != 3)
            {
                throw new ArgumentException("Currency must be a three-letter code.", nameof(currency));
            }
            if (requesterUserId == taskMasterUserId
                || requesterUserId == custodyUserId
                || taskMasterUserId == custodyUserId)
            {
                throw new ArgumentException("Requester, TaskMaster, and custody accounts must be distinct.");
            }

            var existing = await _dbContext.Escrows
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    escrow => escrow.Id == escrowId || escrow.BookingId == bookingId,
                    cancellationToken);
            if (existing != null)
            {
                return EnsureSameEscrow(
                    existing,
                    escrowId,
                    bookingId,
                    amount,
                    currency,
                    requesterUserId,
                    taskMasterUserId,
                    custodyUserId);
            }

            var now = UtcNow();
            var escrow = new EscrowRecord
            {
                Id = escrowId,
                BookingId = bookingId,
                Amount = amount,
                Currency = currency,
                RequesterUserId = requesterUserId,
                TaskMasterUserId = taskMasterUserId,
                CustodyUserId = custodyUserId,
                Status = EscrowRecord.StatusPending,
                CreatedAt = now,
                UpdatedAt = now
            };

            _dbContext.Escrows.Add(escrow);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                return escrow;
            }
            catch (DbUpdateException)
            {
                _dbContext.Entry(escrow).State = EntityState.Detached;
                existing = await _dbContext.Escrows
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.Id == escrowId || candidate.BookingId == bookingId,
                        cancellationToken);
                if (existing == null)
                {
                    throw;
                }

                return EnsureSameEscrow(
                    existing,
                    escrowId,
                    bookingId,
                    amount,
                    currency,
                    requesterUserId,
                    taskMasterUserId,
                    custodyUserId);
            }
        }

        public Task<EscrowRecord?> GetByIdAsync(
            Guid escrowId,
            CancellationToken cancellationToken = default) =>
            _dbContext.Escrows
                .AsNoTracking()
                .SingleOrDefaultAsync(escrow => escrow.Id == escrowId, cancellationToken);

        public Task<EscrowRecord?> GetByBookingIdAsync(
            string bookingId,
            CancellationToken cancellationToken = default) =>
            _dbContext.Escrows
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    escrow => escrow.BookingId == bookingId,
                    cancellationToken);

        public Task<EscrowRecord> MarkFundedAsync(
            Guid escrowId,
            Guid transactionId,
            CancellationToken cancellationToken = default) =>
            TransitionAsync(
                escrowId,
                EscrowRecord.StatusPending,
                EscrowRecord.StatusFunded,
                transactionId,
                cancellationToken);

        public Task<EscrowRecord> MarkReleasedAsync(
            Guid escrowId,
            Guid transactionId,
            CancellationToken cancellationToken = default) =>
            TransitionAsync(
                escrowId,
                EscrowRecord.StatusFunded,
                EscrowRecord.StatusReleased,
                transactionId,
                cancellationToken);

        public Task<EscrowRecord> MarkRefundedAsync(
            Guid escrowId,
            Guid transactionId,
            CancellationToken cancellationToken = default) =>
            TransitionAsync(
                escrowId,
                EscrowRecord.StatusFunded,
                EscrowRecord.StatusRefunded,
                transactionId,
                cancellationToken);

        private async Task<EscrowRecord> TransitionAsync(
            Guid escrowId,
            string expectedStatus,
            string targetStatus,
            Guid transactionId,
            CancellationToken cancellationToken)
        {
            if (escrowId == Guid.Empty)
            {
                throw new ArgumentException("Escrow id is required.", nameof(escrowId));
            }
            if (transactionId == Guid.Empty)
            {
                throw new ArgumentException("Transaction id is required.", nameof(transactionId));
            }

            var now = UtcNow();
            if (_dbContext.Database.IsRelational())
            {
                var updated = targetStatus switch
                {
                    EscrowRecord.StatusFunded => await _dbContext.Escrows
                        .Where(escrow => escrow.Id == escrowId
                            && escrow.Status == expectedStatus
                            && escrow.FundingTransactionId == null)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(escrow => escrow.Status, targetStatus)
                                .SetProperty(escrow => escrow.FundingTransactionId, transactionId)
                                .SetProperty(escrow => escrow.FundedAt, now)
                                .SetProperty(escrow => escrow.UpdatedAt, now),
                            cancellationToken),
                    EscrowRecord.StatusReleased => await _dbContext.Escrows
                        .Where(escrow => escrow.Id == escrowId
                            && escrow.Status == expectedStatus
                            && escrow.ReleaseTransactionId == null
                            && escrow.RefundTransactionId == null)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(escrow => escrow.Status, targetStatus)
                                .SetProperty(escrow => escrow.ReleaseTransactionId, transactionId)
                                .SetProperty(escrow => escrow.ReleasedAt, now)
                                .SetProperty(escrow => escrow.UpdatedAt, now),
                            cancellationToken),
                    EscrowRecord.StatusRefunded => await _dbContext.Escrows
                        .Where(escrow => escrow.Id == escrowId
                            && escrow.Status == expectedStatus
                            && escrow.ReleaseTransactionId == null
                            && escrow.RefundTransactionId == null)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(escrow => escrow.Status, targetStatus)
                                .SetProperty(escrow => escrow.RefundTransactionId, transactionId)
                                .SetProperty(escrow => escrow.RefundedAt, now)
                                .SetProperty(escrow => escrow.UpdatedAt, now),
                            cancellationToken),
                    _ => throw new InvalidOperationException($"Unsupported escrow target status '{targetStatus}'.")
                };

                if (updated == 0)
                {
                    await ThrowTransitionFailureAsync(
                        escrowId,
                        expectedStatus,
                        targetStatus,
                        cancellationToken);
                }

                return await _dbContext.Escrows
                    .AsNoTracking()
                    .SingleAsync(escrow => escrow.Id == escrowId, cancellationToken);
            }

            var record = await _dbContext.Escrows
                .SingleOrDefaultAsync(escrow => escrow.Id == escrowId, cancellationToken);
            ValidateTransition(record, expectedStatus, targetStatus);

            record!.Status = targetStatus;
            record.UpdatedAt = now;
            switch (targetStatus)
            {
                case EscrowRecord.StatusFunded:
                    record.FundingTransactionId = transactionId;
                    record.FundedAt = now;
                    break;
                case EscrowRecord.StatusReleased:
                    record.ReleaseTransactionId = transactionId;
                    record.ReleasedAt = now;
                    break;
                case EscrowRecord.StatusRefunded:
                    record.RefundTransactionId = transactionId;
                    record.RefundedAt = now;
                    break;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return await _dbContext.Escrows
                .AsNoTracking()
                .SingleAsync(escrow => escrow.Id == escrowId, cancellationToken);
        }

        private async Task ThrowTransitionFailureAsync(
            Guid escrowId,
            string expectedStatus,
            string targetStatus,
            CancellationToken cancellationToken)
        {
            var record = await _dbContext.Escrows
                .AsNoTracking()
                .SingleOrDefaultAsync(escrow => escrow.Id == escrowId, cancellationToken);
            ValidateTransition(record, expectedStatus, targetStatus);
            throw new InvalidOperationException(
                $"Escrow could not transition from {expectedStatus} to {targetStatus}.");
        }

        private static void ValidateTransition(
            EscrowRecord? record,
            string expectedStatus,
            string targetStatus)
        {
            if (record == null)
            {
                throw new KeyNotFoundException("Escrow not found.");
            }

            if (record.Status != expectedStatus)
            {
                throw new InvalidOperationException(
                    $"Escrow is {record.Status} and cannot transition to {targetStatus}.");
            }
        }

        private static EscrowRecord EnsureSameEscrow(
            EscrowRecord existing,
            Guid escrowId,
            string bookingId,
            decimal amount,
            string currency,
            string requesterUserId,
            string taskMasterUserId,
            string custodyUserId)
        {
            if (existing.Id != escrowId
                || existing.BookingId != bookingId
                || existing.Amount != amount
                || existing.Currency != currency
                || existing.RequesterUserId != requesterUserId
                || existing.TaskMasterUserId != taskMasterUserId
                || existing.CustodyUserId != custodyUserId)
            {
                throw new InvalidOperationException(
                    "An escrow already exists for this id or booking with different immutable details.");
            }

            return existing;
        }

        private static string RequireValue(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{parameterName} is required.", parameterName);
            }

            return value.Trim();
        }

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;
    }
}
