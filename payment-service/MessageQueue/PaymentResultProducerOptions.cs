namespace payment_service.MessageQueue
{
    public sealed class PaymentResultProducerOptions
    {
        public string BootstrapServers { get; set; } = string.Empty;
        public string ClientId { get; set; } = "payment-service";
        public string Topic { get; set; } = "payment-results";
        public int MessageTimeoutMs { get; set; } = 30000;
    }
}
