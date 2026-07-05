namespace payment_service.ConsulConfig
{
    public class ConsulConfig
    {
        public string ServiceId { get; set; } = Guid.NewGuid().ToString(); // Unique ID for each instance
        public string ServiceName { get; set; } = string.Empty;
        public string ServiceAddress { get; set; } = string.Empty;
        public int ServicePort { get; set; }
        public string HealthCheckUrl { get; set; } = string.Empty;
        public int HealthCheckIntervalSeconds { get; set; } = 10;
        public int HealthCheckTimeoutSeconds { get; set; } = 5;
        public string ConsulAddress { get; set; } = string.Empty;
    }
}
