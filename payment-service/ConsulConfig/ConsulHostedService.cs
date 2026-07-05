using Consul;
using Microsoft.Extensions.Options;

namespace payment_service.ConsulConfig
{
    public class ConsulHostedService : IHostedService
    {
        private readonly IConsulClient _consulClient;
        private readonly ConsulConfig _consulConfig;
        private readonly ILogger<ConsulHostedService> _logger;

        public ConsulHostedService(IConsulClient consulClient, IOptions<ConsulConfig> consulConfig, ILogger<ConsulHostedService> logger)
        {
            _consulClient = consulClient;
            _logger = logger;
            _consulConfig = consulConfig.Value;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Registering service '{ServiceName}' with Consul...", _consulConfig.ServiceName);

            var registration = new AgentServiceRegistration()
            {
                ID = _consulConfig.ServiceId,
                Name = _consulConfig.ServiceName,
                Address = _consulConfig.ServiceAddress,
                Port = _consulConfig.ServicePort,
                Tags = new[] { "payment", "service" },
                Check = new AgentServiceCheck()
                {
                    HTTP = _consulConfig.HealthCheckUrl,
                    Interval = TimeSpan.FromSeconds(_consulConfig.HealthCheckIntervalSeconds),
                    Timeout = TimeSpan.FromSeconds(_consulConfig.HealthCheckTimeoutSeconds)
                }
            };

            await _consulClient.Agent.ServiceDeregister(registration.ID, cancellationToken); // Deregister any existing instance
            await _consulClient.Agent.ServiceRegister(registration, cancellationToken);
            _logger.LogInformation("Service '{ServiceName}' registered with Consul. ID: {ServiceId}", _consulConfig.ServiceName, _consulConfig.ServiceId);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _consulClient.Agent.ServiceDeregister(_consulConfig.ServiceId, cancellationToken);
        }
    }
}
