
namespace calendar_service.Models.ConsulConfig;

public class ConsulConfig
{
    public string ServiceId { get; set; } = Guid.NewGuid().ToString(); // Unique ID for each instance
    public string ServiceName { get; set; }
    public string ServiceAddress { get; set; }
    public int ServicePort { get; set; }
    public string HealthCheckUrl { get; set; }
    public int HealthCheckIntervalSeconds { get; set; } = 10;
    public int HealthCheckTimeoutSeconds { get; set; } = 5;
    public string ConsulAddress { get; set; }
}