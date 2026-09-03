using calendar_service.Model;
using Payment.Contracts.V1;

namespace calendar_service.Services.Contracts
{
    /// <summary>
    /// Owns the escrow side of the booking-payment saga: eligibility checks, custody account
    /// resolution, saga identity and <see cref="PaymentRequestedV1"/> construction. This lives
    /// outside the HTTP layer so REST controllers and future MCP tools enqueue money movement
    /// through exactly the same code path (see AI_HIRING_AGENT_SPEC.md section 6.3).
    /// </summary>
    /// <remarks>
    /// Failures are signalled with exceptions rather than HTTP results:
    /// <see cref="KeyNotFoundException"/> for an unknown booking,
    /// <see cref="UnauthorizedAccessException"/> when the caller is not the paying requester,
    /// <see cref="InvalidOperationException"/> for an ineligible booking state, and
    /// <see cref="EscrowConfigurationException"/> when custody configuration is missing.
    /// </remarks>
    public interface IEscrowPaymentService
    {
        /// <summary>
        /// Validates that the caller may fund the booking, attaches an escrow id when the
        /// booking does not have one yet, and durably enqueues the funding command.
        /// </summary>
        Task<PaymentAcceptedResponseV1> FundEscrowAsync(
            string bookingId,
            string callerUsername,
            string paymentMethodToken,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Durably enqueues a release or refund of an already-funded escrow. The payer is the
        /// custody account; the payee is derived from the operation, never from a caller input.
        /// </summary>
        Task<PaymentAcceptedResponseV1> EnqueueTransferAsync(
            Booking booking,
            string operation,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Throws <see cref="ActivePaymentSagaException"/> when the booking's most recent saga
        /// is a STARTED instance of <paramref name="operation"/>, so a duplicate request is
        /// rejected before any booking mutation is applied.
        /// </summary>
        Task EnsureNoActiveOperationAsync(string bookingId, string operation);
    }
}
