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
        /// Returns the most recently created saga for a booking, if any — used to check
        /// "is a payment for this booking currently pending/ambiguous?" (i.e. STARTED), both
        /// when displaying a booking (GET /api/booking/{id}) and to reject a new /pay attempt
        /// while a previous one is still unresolved. See PAYMENT_SAGA_SPEC.md.
        /// </summary>
        Task<SagaState?> GetLatestByBookingIdAsync(string bookingId);

        /// <summary>
        /// Returns sagas stuck in STARTED for longer than <paramref name="stuckThreshold"/>,
        /// for the reconciliation job to resolve.
        /// </summary>
        Task<List<SagaState>> FindStuckAsync(TimeSpan stuckThreshold);

        /// <summary>
        /// Atomically claims a stuck saga for reconciliation: succeeds only if it's still
        /// STARTED and either unclaimed or its previous claim is older than
        /// <paramref name="claimTtl"/> (so a replica that crashed mid-reconciliation doesn't
        /// block it forever). Returns null if another instance currently holds a live claim —
        /// the caller should skip this saga for the current pass. This is what lets multiple
        /// calendar-service replicas each run an independent reconciliation timer without
        /// duplicating work (or redundant payment-service lookups) for the same saga. Relies on
        /// all replicas sharing one consistent MongoDB deployment — see PAYMENT_SAGA_SPEC.md.
        /// </summary>
        Task<SagaState?> TryClaimAsync(Guid sagaId, TimeSpan claimTtl);
    }
}
