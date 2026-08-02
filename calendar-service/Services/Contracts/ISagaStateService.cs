using calendar_service.Model;
using Payment.Contracts.V1;

namespace calendar_service.Services.Contracts
{
    /// <summary>
    /// Durable saga-state and command-outbox tracking for the booking-payment flow (see
    /// PAYMENT_SAGA_SPEC.md). A STARTED row and its payment request are persisted atomically
    /// before dispatch and resolved to COMPLETED/FAILED after a durable payment result.
    /// </summary>
    public interface ISagaStateService
    {
        /// <summary>
        /// Atomically inserts a STARTED saga and its pending escrow command. A partial unique
        /// index rejects a second active saga for the same booking and operation.
        /// </summary>
        Task<SagaState> EnqueueAsync(
            PaymentRequestedV1 request,
            string? traceParent = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically claims the next eligible undispatched outbox request. A CLAIMED request
        /// becomes eligible again after its lease expires so another replica can recover it.
        /// </summary>
        Task<SagaState?> TryClaimNextDispatchAsync(
            TimeSpan claimLease,
            CancellationToken cancellationToken = default);

        Task<long> GetDispatchBacklogAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a claimed request dispatched only when the supplied claim timestamp still owns
        /// the lease. Returns false if the lease was lost or the request was already resolved.
        /// </summary>
        Task<bool> MarkDispatchedAsync(
            Guid sagaId,
            DateTime claimTimestamp,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases a failed claim back to PENDING with its next eligible retry time. The update
        /// succeeds only for the current lease owner.
        /// </summary>
        Task<bool> RescheduleDispatchAsync(
            Guid sagaId,
            DateTime claimTimestamp,
            DateTime nextAttemptAt,
            string error,
            CancellationToken cancellationToken = default);

        /// <summary>Marks a saga COMPLETED once the payment is verified and the booking updated.</summary>
        Task<SagaState?> CompleteAsync(Guid sagaId, string paymentTransactionId);

        /// <summary>Marks a saga FAILED (declined, mismatch, unreachable, etc).</summary>
        Task<SagaState?> FailAsync(Guid sagaId, string failureReason);

        /// <summary>
        /// Completes a result only while the saga is STARTED. Returns false for a duplicate or
        /// concurrently resolved result.
        /// </summary>
        Task<bool> CompleteResultAsync(
            Guid sagaId,
            string paymentTransactionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Fails a result only while the saga is STARTED and records the transaction that
        /// produced the decline or invalid result.
        /// </summary>
        Task<bool> FailResultAsync(
            Guid sagaId,
            string paymentTransactionId,
            string failureReason,
            CancellationToken cancellationToken = default);

        Task<SagaState?> GetBySagaIdAsync(Guid sagaId);

        /// <summary>
        /// Returns the most recently created saga for a booking, if any — used to check
        /// "is a payment for this booking currently pending/ambiguous?" (i.e. STARTED), both
        /// when displaying a booking (GET /api/booking/{id}) and to reject a new /pay attempt
        /// while a previous one is still unresolved. See PAYMENT_SAGA_SPEC.md.
        /// </summary>
        Task<SagaState?> GetLatestByBookingIdAsync(string bookingId);

        /// <summary>
        /// Returns legacy and escrow sagas stuck in STARTED for longer than
        /// <paramref name="stuckThreshold"/>. Reconciliation distinguishes undispatched escrow
        /// requests from dispatched work awaiting a durable result.
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
