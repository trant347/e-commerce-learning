namespace payment_service.Contracts
{
    public class PaymentMethodTokenResponse
    {
        public required string PaymentMethodToken { get; init; }
        public required DateTime ExpiresAt { get; init; }
    }
}
