namespace payment_service.Services
{
    public class PaymentMethodTokenException : Exception
    {
        public const string Invalid = "INVALID";
        public const string Expired = "EXPIRED";
        public const string AlreadyRedeemed = "ALREADY_REDEEMED";

        public PaymentMethodTokenException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
