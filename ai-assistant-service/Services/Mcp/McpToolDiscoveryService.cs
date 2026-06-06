using ai_assistant_service.Services.Tools;
using ModelContextProtocol.Client;

namespace ai_assistant_service.Services.Mcp;

/// <summary>
/// Background service that connects to configured MCP servers on startup,
/// discovers their tools, and registers them in the <see cref="ToolRegistry"/>.
/// Keeps MCP client connections alive for the application lifetime.
/// </summary>
public sealed class McpToolDiscoveryService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ToolRegistry _toolRegistry;
    private readonly ILogger<McpToolDiscoveryService> _logger;
    private readonly List<McpClient> _clients = new();

    private const int MaxRetryAttempts = 10;

    public McpToolDiscoveryService(
        IConfiguration configuration,
        ToolRegistry toolRegistry,
        ILogger<McpToolDiscoveryService> logger)
    {
        _configuration = configuration;
        _toolRegistry = toolRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var servers = _configuration.GetSection("McpServers").Get<McpServerConfig[]>() ?? [];

        if (servers.Length == 0)
        {
            _logger.LogInformation("[MCP Discovery] No MCP servers configured — skipping tool discovery");
            return;
        }

        _logger.LogInformation("[MCP Discovery] Discovering tools from {Count} MCP server(s)", servers.Length);

        foreach (var server in servers)
        {
            await DiscoverWithRetryAsync(server, stoppingToken);
        }
    }

    private async Task DiscoverWithRetryAsync(McpServerConfig server, CancellationToken stoppingToken)
    {
        for (int attempt = 1; attempt <= MaxRetryAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "[MCP Discovery] Connecting to '{Name}' at {Endpoint} (attempt {Attempt}/{Max})",
                    server.Name, server.Endpoint, attempt, MaxRetryAttempts);

                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(server.Endpoint),
                    TransportMode = HttpTransportMode.Sse,
                    Name = server.Name
                });

                var client = await McpClient.CreateAsync(transport, cancellationToken: stoppingToken);

                lock (_clients)
                {
                    _clients.Add(client);
                }

                var tools = await client.ListToolsAsync(cancellationToken: stoppingToken);

                _logger.LogInformation(
                    "[MCP Discovery] Discovered {ToolCount} tool(s) from '{Name}': {ToolNames}",
                    tools.Count, server.Name,
                    string.Join(", ", tools.Select(t => t.Name)));

                foreach (var mcpTool in tools)
                {
                    var remoteTool = new McpRemoteTool(mcpTool);
                    _toolRegistry.Register(remoteTool);
                    _logger.LogInformation(
                        "[MCP Discovery] Registered remote tool '{ToolName}' from '{ServerName}'",
                        remoteTool.Name, server.Name);
                }

                return; // success
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[MCP Discovery] Failed to connect to '{Name}' (attempt {Attempt}/{Max})",
                    server.Name, attempt, MaxRetryAttempts);

                if (attempt < MaxRetryAttempts)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(attempt * 3, 30));
                    _logger.LogInformation("[MCP Discovery] Retrying in {Delay}s", delay.TotalSeconds);
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }

        _logger.LogError(
            "[MCP Discovery] Exhausted retries for '{Name}' — tools from this server will not be available",
            server.Name);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[MCP Discovery] Shutting down — disposing {Count} MCP client(s)", _clients.Count);

        List<McpClient> clientsCopy;
        lock (_clients)
        {
            clientsCopy = new List<McpClient>(_clients);
            _clients.Clear();
        }

        foreach (var client in clientsCopy)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MCP Discovery] Error disposing MCP client");
            }
        }

        await base.StopAsync(cancellationToken);
    }
}
