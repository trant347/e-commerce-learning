using ai_assistant_service.Services.Tools;
using ModelContextProtocol.Client;

namespace ai_assistant_service.Services.Mcp;

/// <summary>
/// Background service that connects to configured MCP servers on startup,
/// discovers their tools, and registers them in the <see cref="ToolRegistry"/>.
/// Afterwards it periodically refreshes the search tool's category enum so
/// categories added by administrators become selectable without a restart.
/// The MCP SSE client does not reconnect on its own, so a failed refresh (for
/// example after product-service restarts) triggers rediscovery of the server
/// that owns <c>get_categories</c>, restoring both the enum and chat tool calls.
/// </summary>
public sealed class McpToolDiscoveryService : BackgroundService
{
    internal const string CategoryRefreshIntervalKey = "McpDiscovery:CategoryRefreshIntervalSeconds";
    private const int DefaultCategoryRefreshIntervalSeconds = 60;
    private const int MaxRetryAttempts = 10;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    // Replaced clients stay alive briefly so chat tool calls already in flight can finish.
    private static readonly TimeSpan RetiredClientGracePeriod = TimeSpan.FromSeconds(30);

    private readonly IConfiguration _configuration;
    private readonly ToolRegistry _toolRegistry;
    private readonly McpCategoryEnumRefresher _categoryRefresher;
    private readonly ILogger<McpToolDiscoveryService> _logger;

    private readonly object _stateLock = new();
    private readonly Dictionary<string, McpClient> _clientsByServer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _serverByTool = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(McpClient Client, DateTimeOffset RetiredAt)> _retiredClients = new();

    public McpToolDiscoveryService(
        IConfiguration configuration,
        ToolRegistry toolRegistry,
        McpCategoryEnumRefresher categoryRefresher,
        ILogger<McpToolDiscoveryService> logger)
    {
        _configuration = configuration;
        _toolRegistry = toolRegistry;
        _categoryRefresher = categoryRefresher;
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

        await RefreshCategoriesPeriodicallyAsync(servers, stoppingToken);
    }

    private async Task RefreshCategoriesPeriodicallyAsync(
        McpServerConfig[] servers,
        CancellationToken stoppingToken)
    {
        var intervalSeconds = _configuration.GetValue(
            CategoryRefreshIntervalKey,
            DefaultCategoryRefreshIntervalSeconds);
        if (intervalSeconds <= 0)
        {
            _logger.LogInformation(
                "[MCP Discovery] Periodic category refresh and reconnect disabled ({Key}={Value})",
                CategoryRefreshIntervalKey, intervalSeconds);
            return;
        }

        _logger.LogInformation(
            "[MCP Discovery] Refreshing search_task_masters categories every {Interval}s",
            intervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await DisposeExpiredRetiredClientsAsync();
                var result = await _categoryRefresher.RefreshAsync(stoppingToken);
                // NoUsableCategories means the session answered, so it stays connected.
                if (result is CategoryRefreshResult.Failed or CategoryRefreshResult.ToolsUnavailable)
                {
                    await ReconnectAsync(servers, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Reconnects the server that provided <c>get_categories</c> (its session is
    /// presumed dead) and any server that has never connected. One attempt per
    /// server; the next timer tick retries if it still fails.
    /// </summary>
    private async Task ReconnectAsync(McpServerConfig[] servers, CancellationToken stoppingToken)
    {
        string? categoriesOwner;
        HashSet<string> connected;
        lock (_stateLock)
        {
            _serverByTool.TryGetValue(McpCategoryEnumRefresher.CategoriesToolName, out categoriesOwner);
            connected = new HashSet<string>(_clientsByServer.Keys, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var server in servers)
        {
            var ownsCategories = string.Equals(server.Name, categoriesOwner, StringComparison.OrdinalIgnoreCase);
            if (!ownsCategories && connected.Contains(server.Name))
            {
                continue;
            }

            _logger.LogInformation(
                "[MCP Discovery] Reconnecting to '{Name}' at {Endpoint}",
                server.Name, server.Endpoint);
            await TryDiscoverOnceAsync(server, logFailureDetails: false, stoppingToken);
        }
    }

    private async Task DiscoverWithRetryAsync(McpServerConfig server, CancellationToken stoppingToken)
    {
        for (int attempt = 1; attempt <= MaxRetryAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            _logger.LogInformation(
                "[MCP Discovery] Connecting to '{Name}' at {Endpoint} (attempt {Attempt}/{Max})",
                server.Name, server.Endpoint, attempt, MaxRetryAttempts);

            if (await TryDiscoverOnceAsync(server, logFailureDetails: true, stoppingToken))
            {
                return;
            }

            if (attempt < MaxRetryAttempts && !stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 3, 30));
                _logger.LogInformation("[MCP Discovery] Retrying in {Delay}s", delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(
                "[MCP Discovery] Exhausted startup retries for '{Name}' — will keep retrying on the category refresh interval",
                server.Name);
        }
    }

    /// <summary>
    /// Connects to one server, registers its tools (replacing any stale wrappers
    /// bound to a previous session), and re-applies the category enum.
    /// </summary>
    private async Task<bool> TryDiscoverOnceAsync(
        McpServerConfig server,
        bool logFailureDetails,
        CancellationToken stoppingToken)
    {
        McpClient? client = null;
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectCts.CancelAfter(ConnectTimeout);
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Endpoint),
                TransportMode = HttpTransportMode.Sse,
                Name = server.Name
            });

            client = await McpClient.CreateAsync(transport, cancellationToken: connectCts.Token);
            var tools = await client.ListToolsAsync(cancellationToken: connectCts.Token);

            _logger.LogInformation(
                "[MCP Discovery] Discovered {ToolCount} tool(s) from '{Name}': {ToolNames}",
                tools.Count, server.Name,
                string.Join(", ", tools.Select(t => t.Name)));

            McpClient? previousClient;
            lock (_stateLock)
            {
                _clientsByServer.TryGetValue(server.Name, out previousClient);
                _clientsByServer[server.Name] = client;
                foreach (var mcpTool in tools)
                {
                    _serverByTool[mcpTool.Name] = server.Name;
                }
                if (previousClient is not null)
                {
                    _retiredClients.Add((previousClient, DateTimeOffset.UtcNow));
                }
            }

            foreach (var mcpTool in tools)
            {
                var remoteTool = new McpRemoteTool(mcpTool);
                if (string.Equals(remoteTool.Name, McpCategoryEnumRefresher.SearchToolName, StringComparison.OrdinalIgnoreCase))
                {
                    await _categoryRefresher.ApplyKnownCategoriesAsync(remoteTool, stoppingToken);
                }
                _toolRegistry.Register(remoteTool);
                _logger.LogInformation(
                    "[MCP Discovery] Registered remote tool '{ToolName}' from '{ServerName}'",
                    remoteTool.Name, server.Name);
            }

            await _categoryRefresher.RefreshAsync(stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            if (client is not null) await DisposeClientAsync(client);
            return false;
        }
        catch (Exception ex)
        {
            if (client is not null) await DisposeClientAsync(client);

            if (logFailureDetails)
            {
                _logger.LogWarning(ex, "[MCP Discovery] Failed to connect to '{Name}'", server.Name);
            }
            else
            {
                _logger.LogWarning(
                    "[MCP Discovery] Failed to connect to '{Name}' ({Error})",
                    server.Name, ex.Message.Length > 200 ? ex.Message[..200] + "…" : ex.Message);
            }
            return false;
        }
    }

    private async Task DisposeExpiredRetiredClientsAsync()
    {
        List<McpClient> expired;
        var cutoff = DateTimeOffset.UtcNow - RetiredClientGracePeriod;
        lock (_stateLock)
        {
            expired = _retiredClients.Where(r => r.RetiredAt <= cutoff).Select(r => r.Client).ToList();
            _retiredClients.RemoveAll(r => r.RetiredAt <= cutoff);
        }

        foreach (var client in expired)
        {
            await DisposeClientAsync(client);
        }
    }

    private async Task DisposeClientAsync(McpClient client)
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the refresh/reconnect loop first so it cannot add a client after cleanup.
        await base.StopAsync(cancellationToken);

        List<McpClient> clientsCopy;
        lock (_stateLock)
        {
            clientsCopy = _clientsByServer.Values.Concat(_retiredClients.Select(r => r.Client)).ToList();
            _clientsByServer.Clear();
            _retiredClients.Clear();
        }

        _logger.LogInformation("[MCP Discovery] Shutting down — disposing {Count} MCP client(s)", clientsCopy.Count);

        foreach (var client in clientsCopy)
        {
            await DisposeClientAsync(client);
        }
    }
}
