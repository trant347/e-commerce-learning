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
            // Idempotency: if the caller (e.g. calendar-service's saga, or its reconciliation
            // job retrying after a crash) already sent this SagaId, return the original
            // transaction instead of charging again. See PAYMENT_SAGA_SPEC.md, "Idempotency key".
            if (request.SagaId.HasValue)
            {
                var existing = await _dbContext.Transactions
                    .FirstOrDefaultAsync(t => t.SagaId == request.SagaId.Value);
                if (existing != null)
                {
                    _logger.LogInformation("Deduped payment request for sagaId={SagaId}, returning existing transaction {Id}",
                        request.SagaId, existing.Id);
                    return existing;
                }
            }

            // Wrap the whole charge (wallet check/debit/credit) + record write in one explicit
            // transaction, so the payment record and wallet movement are committed atomically
            // and consistently (ACID). This also gives the gateway's row lock (see
            // WalletSimulationPaymentGateway) an actual transaction to hold the lock within —
            // without one, two concurrent charges against the same wallet could both read the
            // same balance, both pass the "can afford it" check, and both deduct.
            await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync();

            // The actual "will this charge succeed" decision (and any money movement it implies)
            // is delegated to the gateway (see IPaymentGateway/WalletSimulationPaymentGateway),
            // so a real payment processor could be swapped in later without touching this
            // orchestration/persistence logic. Round explicitly (banker's rounding, matching the
            // numeric(18,2) column) so the value returned to the caller always matches exactly
            // what was persisted.
            var gatewayResult = await _gateway.ChargeAsync(request);
            var transaction = new PaymentTransaction
            {
                Amount = Math.Round(request.Amount, 2, MidpointRounding.ToEven),
                Currency = request.Currency,
                MaskedCardNumber = MaskCardNumber(request.CreditCard.CardNumber),
                OwnerName = request.CreditCard.OwnerName,
                Status = gatewayResult.Status,
                DeclineReason = gatewayResult.DeclineReason,
                SagaId = request.SagaId
            };

            _dbContext.Transactions.Add(transaction);
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
                var existing = await _dbContext.Transactions
                    .FirstOrDefaultAsync(t => t.SagaId == request.SagaId.Value);
                if (existing != null)
                {
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

        private static string MaskCardNumber(string cardNumber)
        {
            if (string.IsNullOrEmpty(cardNumber) || cardNumber.Length <= 4)
            {
                return "****";
            }

            return new string('*', cardNumber.Length - 4) + cardNumber[^4..];
        }

        public Task<PaymentTransaction?> GetTransactionBySagaIdAsync(Guid sagaId) =>
            _dbContext.Transactions.FirstOrDefaultAsync(t => t.SagaId == sagaId);
    }
}
