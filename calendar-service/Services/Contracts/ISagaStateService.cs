using calendar_service.Model;

namespace calendar_service.Services.Contracts
{
    /// <summary>
    /// Durable saga-state tracking for the booking-payment flow (see PAYMENT_SAGA_SPEC.md).
    /// A row is written STARTED before calling out to payment-service and resolved to
    /// COMPLETED/FAILED afterwards, so a crash mid-flight can be recovered by the
    /// reconciliation job instead of leaving a silent inconsistency.
    /// </summary>
    public interface ISagaStateService
    {
        /// <summary>Writes a new STARTED saga row before the payment-service call is made.</summary>
        Task<SagaState> StartAsync(string bookingId, Guid sagaId, decimal requestedAmount);

        /// <summary>Marks a saga COMPLETED once the payment is verified and the booking updated.</summary>
        Task<SagaState?> CompleteAsync(Guid sagaId, string paymentTransactionId);

        /// <summary>Marks a saga FAILED (declined, mismatch, unreachable, etc).</summary>
        Task<SagaState?> FailAsync(Guid sagaId, string failureReason);

        Task<SagaState?> GetBySagaIdAsync(Guid sagaId);

        /// <summary>
        /// Returns sagas stuck in STARTED for longer than <paramref name="stuckThreshold"/>,
        /// for the reconciliation job to resolve.
        /// </summary>
        Task<List<SagaState>> FindStuckAsync(TimeSpan stuckThreshold);
    }
}
