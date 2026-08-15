using payment_service.Contracts;
using payment_service.Data;
using payment_service.Models;
using Microsoft.EntityFrameworkCore;

namespace payment_service.Services
{
    public class PaymentService : IPaymentService
    {
        private readonly PaymentDbContext _dbContext;
        private readonly IPaymentGateway _gateway;
        private readonly ILogger<PaymentService> _logger;

        public PaymentService(PaymentDbContext dbContext, IPaymentGateway gateway, ILogger<PaymentService> logger)
        {
            _dbContext = dbContext;
            _gateway = gateway;
            _logger = logger;
        }

        public async Task<PaymentTransaction> ProcessPaymentAsync(PaymentRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            request.Currency = request.Currency.Trim().ToUpperInvariant();
            request.PayerUserId = NormalizeOptional(request.PayerUserId);
            request.PayeeUserId = NormalizeOptional(request.PayeeUserId);
            request.Amount = Math.Round(
                request.Amount,
                2,
                MidpointRounding.ToEven);

            // Idempotency: if the caller (e.g. calendar-service's saga, or its reconciliation
            // job retrying after a crash) already sent this SagaId, return the original
            // transaction instead of charging again. See PAYMENT_SAGA_SPEC.md, "Idempotency key".
            if (request.SagaId.HasValue)
            {
                var existing = await _dbContext.Transactions
                    .FirstOrDefaultAsync(t => t.SagaId == request.SagaId.Value);
                if (existing != null)
                {
                    EnsureSameRequest(existing, request);
                    _logger.LogInformation("Deduped payment request for sagaId={SagaId}, returning existing transaction {Id}",
                        request.SagaId, existing.Id);
                    return existing;
                }
            }

            // Wrap the whole charge (wallet check/debit/credit) + record write in one explicit
            // transaction, so the payment record, journal posting, and balance projections are
            // committed atomically. LedgerService joins this transaction and holds its
            // deterministic account/projection locks until the final commit.
            await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync();

            var transaction = new PaymentTransaction
            {
                Amount = request.Amount,
                Currency = request.Currency,
                MaskedCardNumber = PaymentCardUtility.Mask(request.CreditCard.CardNumber),
                OwnerName = request.CreditCard.OwnerName,
                Status = PaymentTransaction.StatusApproved,
                SagaId = request.SagaId,
                Operation = JournalEntry.OperationLegacyPayment,
                PayerUserId = request.PayerUserId,
                PayeeUserId = request.PayeeUserId
            };
            _dbContext.Transactions.Add(transaction);

            // The actual "will this charge succeed" decision (and any money movement it implies)
            // is delegated to the gateway (see IPaymentGateway/WalletSimulationPaymentGateway),
            // so a real payment processor could be swapped in later without touching this
            // orchestration/persistence logic. Round explicitly (banker's rounding, matching the
            // numeric(18,2) column) so the value returned to the caller always matches exactly
            // what was persisted.
            var idempotencyKey = request.SagaId.HasValue
                ? $"{JournalEntry.OperationLegacyPayment}:{request.SagaId.Value:D}"
                : $"{JournalEntry.OperationLegacyPayment}:{transaction.Id:D}";
            PaymentGatewayResult gatewayResult;
            try
            {
                gatewayResult = await _gateway.ChargeAsync(
                    request,
                    new PaymentGatewayContext(transaction.Id, idempotencyKey));
            }
            catch (OperationCanceledException)
            {
                await dbTransaction.RollbackAsync();
                throw;
            }
            catch (Exception) when (request.SagaId.HasValue)
            {
                await dbTransaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                var existing = await _dbContext.Transactions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.SagaId == request.SagaId.Value);
                if (existing == null)
                {
                    throw;
                }

                EnsureSameRequest(existing, request);
                return existing;
            }

            transaction.Status = gatewayResult.Status;
            transaction.DeclineReason = gatewayResult.DeclineReason;
            try
            {
                await _dbContext.SaveChangesAsync();
                await dbTransaction.CommitAsync();
            }
            catch (DbUpdateException) when (request.SagaId.HasValue)
            {
                // Two concurrent requests for the same SagaId raced past the earlier existence
                // check; the unique index rejected the second insert. Roll back and return the
                // transaction the other request just committed, rather than surfacing an error.
                await dbTransaction.RollbackAsync();
                _dbContext.ChangeTracker.Clear();
                var existing = await _dbContext.Transactions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.SagaId == request.SagaId.Value);
                if (existing != null)
                {
                    EnsureSameRequest(existing, request);
                    _logger.LogInformation("Deduped concurrent payment request for sagaId={SagaId}, returning existing transaction {Id}",
                        request.SagaId, existing.Id);
                    return existing;
                }
                throw;
            }

            _logger.LogInformation("Recorded payment transaction {Id} for {Amount} {Currency} with status {Status}",
                transaction.Id, transaction.Amount, transaction.Currency, transaction.Status);

            return transaction;
        }

        /// <summary>
        /// Dev/testing-only "magic" test card number that deterministically simulates a
        /// declined charge. Kept here as a forwarding alias to
        /// <see cref="WalletSimulationPaymentGateway.SimulatedDeclineCardNumber"/> for existing
        /// callers/tests; the actual decline decision now lives in the gateway.
        /// </summary>
        public const string SimulatedDeclineCardNumber = WalletSimulationPaymentGateway.SimulatedDeclineCardNumber;

        public Task<PaymentTransaction?> GetTransactionBySagaIdAsync(Guid sagaId) =>
            _dbContext.Transactions.FirstOrDefaultAsync(t => t.SagaId == sagaId);

        private static string? NormalizeOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static void EnsureSameRequest(
            PaymentTransaction transaction,
            PaymentRequest request)
        {
            if (transaction.Amount != request.Amount
                || transaction.Currency != request.Currency
                || !string.Equals(
                    transaction.PayerUserId,
                    request.PayerUserId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    transaction.PayeeUserId,
                    request.PayeeUserId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SagaId {request.SagaId:D} was already used for a different payment request.");
            }
        }
    }
}
