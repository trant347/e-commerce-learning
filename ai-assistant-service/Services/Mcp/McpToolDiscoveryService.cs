using System.Text.Json;
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

                await EnrichSearchToolWithCategoriesAsync(stoppingToken);

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

    /// <summary>
    /// Looks up <c>get_categories</c> and <c>search_task_masters</c> in the registry
    /// and inlines the live category list as an <c>enum</c> on the search tool's
    /// <c>category</c> parameter. Small models (e.g. llama3.2:3b) are dramatically more
    /// reliable at picking the right value when the schema shows the legal options
    /// directly, instead of relying on a multi-call chain (call get_categories, then
    /// search) which they routinely skip.
    /// </summary>
    private async Task EnrichSearchToolWithCategoriesAsync(CancellationToken stoppingToken)
    {
        var searchTool = _toolRegistry.Get("search_task_masters") as McpRemoteTool;
        var categoriesTool = _toolRegistry.Get("get_categories");
        if (searchTool is null || categoriesTool is null)
        {
            _logger.LogInformation(
                "[MCP Discovery] Skipping category enum enrichment — required tools not present");
            return;
        }

        try
        {
            var raw = await categoriesTool.ExecuteAsync(
                new Dictionary<string, string>(),
                stoppingToken);

            var categories = ParseCategories(raw);
            if (categories.Count == 0)
            {
                _logger.LogWarning(
                    "[MCP Discovery] get_categories returned no usable values — skipping enum injection");
                return;
            }

            searchTool.SetParameterEnum("category", categories);
            _logger.LogInformation(
                "[MCP Discovery] Injected {Count} categories into search_task_masters schema",
                categories.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MCP Discovery] Failed to enrich search tool with categories");
        }
    }

    private static IReadOnlyList<string> ParseCategories(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // MCP wraps a tool's String result as a JSON-encoded string, so we may
            // receive "[\"a\",\"b\"]" (a string) instead of ["a","b"] (an array).
            // Unwrap one layer and re-parse if needed.
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner)) return Array.Empty<string>();
                using var innerDoc = JsonDocument.Parse(inner);
                return ExtractStringArray(innerDoc.RootElement);
            }

            return ExtractStringArray(root);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ExtractStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        return element.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
