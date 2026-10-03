using System.Net;
using System.Text.Json;
using ai_assistant_service.Services.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ai_assistant_service.Tests;

public class ReadinessEndpointTests
{
    private const string JwtSecret = "test-jwt-secret-that-is-at-least-32-bytes";
    private const string Version =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Ready_InLegacyModeReportsCatalogNotRequired()
    {
        await using var factory = new ReadinessFactory("legacy");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalog = await ReadCatalogAsync(response);
        Assert.False(catalog.GetProperty("required").GetBoolean());
        Assert.True(catalog.GetProperty("usable").GetBoolean());
    }

    [Fact]
    public async Task Ready_InRichModeWithoutSnapshotReturnsServiceUnavailable()
    {
        await using var factory = new ReadinessFactory("catalog-llm");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var catalog = await ReadCatalogAsync(response);
        Assert.True(catalog.GetProperty("required").GetBoolean());
        Assert.False(catalog.GetProperty("usable").GetBoolean());
    }

    [Fact]
    public async Task Ready_InRichModeWithFreshSnapshotReturnsOk()
    {
        await using var factory = new ReadinessFactory("hybrid");
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<CategoryCatalogSnapshotStore>().Publish(
            new CategoryCatalogSnapshot(
                1,
                Version,
                [new CategoryCatalogItem("carpentry", "Carpentry", "Wood construction.", [])],
                "{}",
                DateTimeOffset.UtcNow));

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalog = await ReadCatalogAsync(response);
        Assert.True(catalog.GetProperty("usable").GetBoolean());
        Assert.Equal(Version, catalog.GetProperty("catalogVersion").GetString());
    }

    private static async Task<JsonElement> ReadCatalogAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("categoryCatalog").Clone();
    }

    private sealed class ReadinessFactory(string mode) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JwtSettings:Secret", JwtSecret);
            builder.UseSetting("CategoryResolution:Mode", mode);
            builder.UseSetting("McpDiscovery:CategoryRefreshIntervalSeconds", "60");
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
