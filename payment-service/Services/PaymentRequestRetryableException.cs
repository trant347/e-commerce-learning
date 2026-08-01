namespace payment_service.Services
{
    /// <summary>
    /// Identifies a payment request failure that should be retried instead of dead-lettered.
    /// </summary>
    public sealed class PaymentRequestRetryableException : Exception
    {
        public PaymentRequestRetryableException(string message)
            : base(message)
        {
        }
    }
}
