namespace calendar_service.MessageQueue
{
    public class KafkaProducerConfig
    {
        public string BootstrapServers { get; set; }
        public string ClientId { get; set; }
        public string NotificationTopic { get; set; } = "notification-events";
        public string PaymentRequestTopic { get; set; } = "payment-requests";
        public int PaymentRequestMessageTimeoutMs { get; set; } = 10000;
    }
}