using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ai_assistant_service.Auth;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace ai_assistant_service.Tests;

public sealed class AuthenticationTests : IClassFixture<AuthenticationTests.AiAssistantFactory>
{
    private const string JwtSecret = "test-jwt-secret-that-is-at-least-32-bytes";
    private readonly AiAssistantFactory _factory;

    public AuthenticationTests(AiAssistantFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Chat_WithValidToken_UsesClaimsIdentityAndIgnoresBodyUserId()
    {
        _factory.Assistant.Reset();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken("alice", ["ROLE_USER"]));

        var response = await client.PostAsJsonAsync(
            "/api/ai-assistant/chat",
            new { message = "hello", userId = "mallory" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("alice", _factory.Assistant.CurrentUser?.Username);
        Assert.True(_factory.Assistant.CurrentUser?.IsInRole("ROLE_USER"));
    }

    [Fact]
    public async Task Chat_WithoutToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/ai-assistant/chat",
            new { message = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithMalformedToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.PostAsJsonAsync(
            "/api/ai-assistant/chat",
            new { message = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithInvalidSignature_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                CreateToken("alice", ["ROLE_USER"], secret: "different-test-secret-that-is-32-bytes"));

        var response = await client.PostAsJsonAsync(
            "/api/ai-assistant/chat",
            new { message = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithExpiredToken_ReturnsUnauthorized()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                CreateToken("alice", ["ROLE_USER"], DateTime.UtcNow.AddMinutes(-1)));

        var response = await client.PostAsJsonAsync(
            "/api/ai-assistant/chat",
            new { message = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithTokenMissingSubject_ReturnsForbidden()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(null, ["ROLE_USER"]));

        var response = await client.PostAsJsonAsync(
            "/api/ai-assistant/chat",
            new { message = "hello" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithoutToken_RemainsPublic()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string CreateToken(
        string? username,
        IReadOnlyCollection<string> roles,
        DateTime? expires = null,
        string secret = JwtSecret)
    {
        var claims = roles.Select(role => new Claim("authorities", role)).ToList();
        if (username is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, username));
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: expires ?? DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public sealed class AiAssistantFactory : WebApplicationFactory<Program>
    {
        public RecordingAssistant Assistant { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JwtSettings:Secret", JwtSecret);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["JwtSettings:Secret"] = JwtSecret
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IAiAssistantService>();
                services.AddSingleton<IAiAssistantService>(Assistant);
            });
        }
    }

    public sealed class RecordingAssistant : IAiAssistantService
    {
        public CurrentUser? CurrentUser { get; private set; }

        public Task<ChatResponse> ChatAsync(
            ChatRequest request,
            CurrentUser currentUser,
            CancellationToken cancellationToken)
        {
            CurrentUser = currentUser;
            return Task.FromResult(new ChatResponse
            {
                Answer = "ok",
                Model = "test"
            });
        }

        public void Reset()
        {
            CurrentUser = null;
        }
    }
}
