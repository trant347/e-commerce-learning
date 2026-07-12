using payment_service.Contracts;
using payment_service.Models;

namespace payment_service.Services
{
    public interface IPaymentService
    {
        Task<PaymentTransaction> ProcessPaymentAsync(PaymentRequest request);

        /// <summary>
        /// Looks up the transaction resulting from a given sagaId, if one exists. Used by the
        /// calling saga's reconciliation job to check "did this charge actually happen?" after
        /// a crash left a SagaState stuck in STARTED. See PAYMENT_SAGA_SPEC.md.
        /// </summary>
        Task<PaymentTransaction?> GetTransactionBySagaIdAsync(Guid sagaId);
    }
}
