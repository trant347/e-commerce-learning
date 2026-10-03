using ai_assistant_service.Services.Mcp;
using Xunit;

namespace ai_assistant_service.Tests;

public class McpReconnectSelectionTests
{
    private static readonly McpServerConfig[] Servers =
    [
        new() { Name = "product-service", Endpoint = "http://product-service/sse" },
        new() { Name = "other-service", Endpoint = "http://other-service/sse" },
        new() { Name = "late-service", Endpoint = "http://late-service/sse" }
    ];

    [Fact]
    public void SelectServersToReconnect_SelectsCatalogOwnerAndNeverConnectedServers()
    {
        var connected = new HashSet<string>(
            ["product-service", "other-service"],
            StringComparer.OrdinalIgnoreCase);

        var selected = McpToolDiscoveryService.SelectServersToReconnect(
            Servers,
            "PRODUCT-SERVICE",
            connected);

        Assert.Equal(["product-service", "late-service"], selected.Select(s => s.Name));
    }

    [Fact]
    public void SelectServersToReconnect_WithoutKnownOwnerSelectsEveryServer()
    {
        var connected = new HashSet<string>(
            Servers.Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);

        var selected = McpToolDiscoveryService.SelectServersToReconnect(
            Servers,
            catalogOwner: null,
            connected);

        Assert.Equal(Servers.Select(s => s.Name), selected.Select(s => s.Name));
    }
}
