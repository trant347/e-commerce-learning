namespace payment_service.Contracts
{
    public class CreditCardInfo
    {
        public required string CardNumber { get; set; }
        public required string ExpiryDate { get; set; }
        public required string CVV { get; set; }
        public required string OwnerName { get; set; }
    }
}