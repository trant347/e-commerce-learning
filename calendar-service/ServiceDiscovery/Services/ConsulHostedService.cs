using Consul;
using Microsoft.Extensions.Options;
using calendar_service.Models.ConsulConfig;

public class ConsulHostedService : IHostedService
{
    private readonly IConsulClient _consulClient;
    private readonly ConsulConfig _consulConfig;
    private readonly ILogger<ConsulHostedService> _logger;
    private string _registrationId;

    public ConsulHostedService(IConsulClient consulClient, IOptions<ConsulConfig> consulConfig, ILogger<ConsulHostedService> logger)
    {
        _consulClient = consulClient;
        _logger = logger;
        _consulConfig = consulConfig.Value;
        _registrationId = $"{_consulConfig.ServiceName}-{_consulConfig.ServiceId}";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Registering service '{_consulConfig.ServiceName}' with Consul...");

        var registration = new AgentServiceRegistration()
        {
            ID = _consulConfig.ServiceId,
            Name = _consulConfig.ServiceName,
            Address = _consulConfig.ServiceAddress,
            Port = _consulConfig.ServicePort,
            Tags = new[] { "calendar", "service" },
            Check = new AgentServiceCheck()
            {
                HTTP = _consulConfig.HealthCheckUrl,
                Interval = TimeSpan.FromSeconds(_consulConfig.HealthCheckIntervalSeconds),
                Timeout = TimeSpan.FromSeconds(_consulConfig.HealthCheckTimeoutSeconds)
            }
        };

        await _consulClient.Agent.ServiceDeregister(registration.ID, cancellationToken); // Deregister any existing instance
        await _consulClient.Agent.ServiceRegister(registration, cancellationToken);
        _logger.LogInformation($"Service '{_consulConfig.ServiceName}' registered with Consul. ID: {_registrationId}");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _consulClient.Agent.ServiceDeregister(_consulConfig.ServiceId);
    }
}