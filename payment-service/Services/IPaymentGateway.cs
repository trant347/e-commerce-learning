using payment_service.Contracts;

namespace payment_service.Services
{
    /// <summary>
    /// Outcome of a gateway charge attempt: just enough for PaymentService to decide the
    /// transaction's Status, without the gateway needing to know about persistence.
    /// </summary>
    public class PaymentGatewayResult
    {
        public required string Status { get; set; }

        /// <summary>Human-readable reason for a decline, for logging/diagnostics only.</summary>
        public string? DeclineReason { get; set; }
    }

    /// <summary>
    /// Decides whether a charge is approved or declined, and (for approved charges) moves
    /// money between payer and payee. This is the one seam PaymentService depends on for "did
    /// the charge succeed" — swap in a real payment processor by adding a new implementation
    /// of this interface instead of touching PaymentService's orchestration/persistence logic.
    /// See WalletSimulationPaymentGateway for the current (simulated) implementation.
    /// </summary>
    public interface IPaymentGateway
    {
        Task<PaymentGatewayResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default);
    }
}
