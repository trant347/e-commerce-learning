namespace worker_service.MessageQueue
{
    public class KafkaProducerConfig
    {
        public string BootstrapServers { get; set; } = string.Empty;
        public List<string> Topics { get; set; } = new();
        public List<string> OutputTopics { get; set; } = new();
        public string ClientId { get; set; } = string.Empty;
    }
}
