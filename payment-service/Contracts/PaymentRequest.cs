namespace payment_service.Contracts
{
public class PaymentRequest
    {
        public required CreditCardInfo CreditCard { get; set; }
        public required decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";

        /// <summary>
        /// Username of the user being charged. Used by the wallet-balance payment simulation
        /// (see WalletSimulationPaymentGateway) to decide whether the charge can be approved.
        /// Optional for backward compatibility: requests that omit it skip the wallet-balance
        /// check entirely and are approved as before (unless the simulated-decline test card is
        /// used).
        /// </summary>
        public string? PayerUserId { get; set; }

        /// <summary>
        /// Username of the user receiving the funds (e.g. the TaskMaster being paid). Optional;
        /// when supplied and the charge is approved, the amount is credited to this user's
        /// wallet as well as debited from <see cref="PayerUserId"/>'s.
        /// </summary>
        public string? PayeeUserId { get; set; }

        /// <summary>
        /// Idempotency key supplied by the caller's saga (see PAYMENT_SAGA_SPEC.md). When set,
        /// a retried request with the same SagaId returns the original transaction instead of
        /// charging again.
        /// </summary>
        public Guid? SagaId { get; set; }
    }
}