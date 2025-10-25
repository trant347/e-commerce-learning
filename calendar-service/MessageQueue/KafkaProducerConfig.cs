namespace calendar_service.MessageQueue
{
    public class KafkaProducerConfig
    {
        public string BootstrapServers { get; set; }
        public string ClientId { get; set; }
        public string Topic { get; set; }
    }
}