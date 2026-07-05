namespace payment_service.Contracts
{
public class PaymentRequest
    {
        public required CreditCardInfo CreditCard { get; set; }
        public required decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
    }
}