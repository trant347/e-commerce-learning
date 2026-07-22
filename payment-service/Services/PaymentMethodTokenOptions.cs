namespace payment_service.Services
{
    public class PaymentMethodTokenOptions
    {
        public int LifetimeSeconds { get; set; } = 300;
        public int CleanupIntervalSeconds { get; set; } = 300;
        public int RetentionSeconds { get; set; } = 86400;
    }
}
