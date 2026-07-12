namespace payment_service.Contracts
{
public class PaymentRequest
    {
        public required CreditCardInfo CreditCard { get; set; }
        public required decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";

        /// <summary>
        /// Idempotency key supplied by the caller's saga (see PAYMENT_SAGA_SPEC.md). When set,
        /// a retried request with the same SagaId returns the original transaction instead of
        /// charging again.
        /// </summary>
        public Guid? SagaId { get; set; }
    }
}