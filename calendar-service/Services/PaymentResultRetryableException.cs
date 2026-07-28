namespace calendar_service.Services
{
    public sealed class PaymentResultRetryableException : Exception
    {
        public PaymentResultRetryableException(string message)
            : base(message)
        {
        }
    }
}
