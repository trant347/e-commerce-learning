using payment_service.Contracts;
using payment_service.Data;
using payment_service.Models;
using Microsoft.EntityFrameworkCore;

namespace payment_service.Services
{
    public class PaymentService : IPaymentService
    {
        private readonly PaymentDbContext _dbContext;
        private readonly ILogger<PaymentService> _logger;

        public PaymentService(PaymentDbContext dbContext, ILogger<PaymentService> logger)
        {
            _dbContext = dbContext;
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

            // NOTE: This is a placeholder for real payment processing (e.g. calling out to a
            // payment gateway). It always approves the payment and persists a record.
            // Round explicitly (banker's rounding, matching the numeric(18,2) column) so the
            // value returned to the caller always matches exactly what was persisted.
            var transaction = new PaymentTransaction
            {
                Amount = Math.Round(request.Amount, 2, MidpointRounding.ToEven),
                Currency = request.Currency,
                MaskedCardNumber = MaskCardNumber(request.CreditCard.CardNumber),
                OwnerName = request.CreditCard.OwnerName,
                Status = PaymentTransaction.StatusApproved,
                SagaId = request.SagaId
            };

            // Wrap the write in an explicit transaction so the payment record is committed
            // atomically and consistently (ACID), even once additional writes (e.g. ledger
            // entries) are added alongside it in the future.
            await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync();
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

            _logger.LogInformation("Recorded payment transaction {Id} for {Amount} {Currency}", transaction.Id, transaction.Amount, transaction.Currency);

            return transaction;
        }

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
