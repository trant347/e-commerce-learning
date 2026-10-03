using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Mcp;
using Microsoft.Extensions.Options;
using Xunit;

namespace ai_assistant_service.Tests;

public class JevDecisionClientTests
{
    private const string CatalogVersion =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Endpoint = "https://openrouter.ai/api/alpha/decisions";

    [Fact]
    public async Task Decide_SendsFixedQuestionsCatalogAndSharedKey_NotChatPayload()
    {
        using var handler = new CapturingHandler((_, _) => Task.FromResult(Reply(ValidAnswer)));
        using var http = new HttpClient(handler);

        var decision = await Client(http).DecideAsync(Request(), CancellationToken.None);

        Assert.Equal(new Uri(Endpoint), handler.Uri);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("Bearer", handler.Auth?.Scheme);
        Assert.Equal("fake-key", handler.Auth?.Parameter);
        Assert.Equal("carpentry", decision.Category.Choice);
        Assert.Equal(0.8, decision.Category.Probabilities["carpentry"]);
        Assert.Equal("service_search", decision.Intent.Choice);
        Assert.Equal("typesafe/jev-1.13-20260917", decision.ResolvedModel);
        Assert.Equal(12, decision.InputTokens);

        using var sent = JsonDocument.Parse(handler.Body!);
        var root = sent.RootElement;
        Assert.Equal("typesafe/jev-1.13", root.GetProperty("model").GetString());
        Assert.Equal("Please repair my table", root.GetProperty("state").GetProperty("userRequest").GetString());
        Assert.Equal("I meant wood", root.GetProperty("state").GetProperty("clarificationHistory")[0].GetString());
        Assert.Equal(CatalogVersion, root.GetProperty("state").GetProperty("catalog").GetProperty("catalogVersion").GetString());
        Assert.Equal("choice", root.GetProperty("questions").GetProperty("intent").GetProperty("type").GetString());
        var intent = root.GetProperty("questions").GetProperty("intent");
        Assert.Contains("clarification history", intent.GetProperty("instructions").GetString());
        Assert.Contains("booking a provider", intent.GetProperty("criteria").GetProperty("service_search").GetString());
        Assert.Contains("existing booking", intent.GetProperty("criteria").GetProperty("not_applicable").GetString());
        var criteria = root.GetProperty("questions").GetProperty("category").GetProperty("criteria");
        Assert.Equal(3, criteria.EnumerateObject().Count());
        Assert.Contains("Wood repair", criteria.GetProperty("carpentry").GetString());
        Assert.Contains("woodworking", criteria.GetProperty("carpentry").GetString());
        Assert.True(criteria.TryGetProperty(JevDecisionClient.ClarificationOption, out _));
        Assert.True(criteria.TryGetProperty(JevDecisionClient.NoMatchOption, out _));
        foreach (var forbidden in new[] { "messages", "tools", "tool_choice", "think", "max_tokens" })
            Assert.False(root.TryGetProperty(forbidden, out _));
    }

    [Theory]
    [InlineData(400, JevFailureCategory.InvalidRequest)]
    [InlineData(401, JevFailureCategory.Unauthorized)]
    [InlineData(402, JevFailureCategory.InsufficientCredits)]
    [InlineData(403, JevFailureCategory.Forbidden)]
    [InlineData(413, JevFailureCategory.PayloadTooLarge)]
    [InlineData(429, JevFailureCategory.RateLimited)]
    [InlineData(502, JevFailureCategory.ProviderUnavailable)]
    [InlineData(503, JevFailureCategory.ProviderUnavailable)]
    [InlineData(524, JevFailureCategory.Timeout)]
    [InlineData(529, JevFailureCategory.ProviderUnavailable)]
    public async Task Decide_MapsStatusWithoutExposingBodyOrRetry(
        int status, JevFailureCategory expected)
    {
        using var handler = new CapturingHandler((_, _) => Task.FromResult(Reply(
            """{"error":{"message":"private prompt and secret"}}""", (HttpStatusCode)status)));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<JevDecisionException>(
            () => Client(http).DecideAsync(Request(), CancellationToken.None));

        Assert.Equal(expected, error.Category);
        Assert.Equal(status, error.HttpStatusCode);
        Assert.DoesNotContain("private", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("""{"error":{"code":400,"message":"private prompt"}}""", JevFailureCategory.ProtocolError)]
    [InlineData("""{"answers":{"intent":{"type":"choice"}}}""", JevFailureCategory.MalformedDecision)]
    [InlineData("""{"answers":{},"answers":{},"model":"m","usage":{"input_tokens":1,"output_tokens":1}}""", JevFailureCategory.MalformedDecision)]
    [InlineData("""{"model":"m","answers":{"intent":{"type":"choice","choice":"service_search","confidence":0.9,"probabilities":{"service_search":0.8,"not_applicable":0.2}}},"usage":{"input_tokens":1,"output_tokens":1}}""", JevFailureCategory.MalformedDecision)]
    public async Task Decide_RejectsInvalidEnvelope(string response, JevFailureCategory category)
    {
        using var handler = new CapturingHandler((_, _) => Task.FromResult(Reply(response)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<JevDecisionException>(
            () => Client(http).DecideAsync(Request(), CancellationToken.None));
        Assert.Equal(category, error.Category);
    }

    [Theory]
    [InlineData("\"carpentry\":0.8,\"decision:needs_clarification\":0.1,\"decision:no_match\":0.1", true)]
    [InlineData("\"carpentry\":0.8005,\"decision:needs_clarification\":0.1,\"decision:no_match\":0.1", true)]
    [InlineData("\"carpentry\":0.801,\"decision:needs_clarification\":0.1,\"decision:no_match\":0.1", true)]
    [InlineData("\"carpentry\":0.8011,\"decision:needs_clarification\":0.1,\"decision:no_match\":0.1", false)]
    [InlineData("\"carpentry\":0.8,\"decision:needs_clarification\":0.1", false)]
    [InlineData("\"carpentry\":0.8,\"decision:needs_clarification\":0.1,\"other\":0.1", false)]
    [InlineData("\"carpentry\":0.8,\"carpentry\":0.8,\"decision:no_match\":0.2", false)]
    [InlineData("\"carpentry\":1.01,\"decision:needs_clarification\":0,\"decision:no_match\":0", false)]
    [InlineData("\"carpentry\":0.1,\"decision:needs_clarification\":0.8,\"decision:no_match\":0.1", false)]
    public async Task Decide_EnforcesExactDistributionAndTolerance(string probabilities, bool valid)
    {
        var answer = ValidAnswer.Replace(
            "\"carpentry\":0.8,\"decision:needs_clarification\":0.1,\"decision:no_match\":0.1",
            probabilities, StringComparison.Ordinal);
        using var handler = new CapturingHandler((_, _) => Task.FromResult(Reply(answer)));
        using var http = new HttpClient(handler);
        if (valid)
            Assert.Equal("carpentry", (await Client(http).DecideAsync(Request(), CancellationToken.None)).Category.Choice);
        else
            Assert.Equal(JevFailureCategory.MalformedDecision,
                (await Assert.ThrowsAsync<JevDecisionException>(
                    () => Client(http).DecideAsync(Request(), CancellationToken.None))).Category);
    }

    [Theory]
    [InlineData("\"confidence\":1.01")]
    [InlineData("\"confidence\":-0.1")]
    [InlineData("\"confidence\":1e999")]
    [InlineData("\"confidence\":\"0.8\"")]
    public async Task Decide_RejectsInvalidConfidence(string confidence)
    {
        using var handler = new CapturingHandler((_, _) => Task.FromResult(Reply(
            ValidAnswer.Replace("\"confidence\":0.75", confidence, StringComparison.Ordinal))));
        using var http = new HttpClient(handler);
        Assert.Equal(JevFailureCategory.MalformedDecision,
            (await Assert.ThrowsAsync<JevDecisionException>(
                () => Client(http).DecideAsync(Request(), CancellationToken.None))).Category);
    }

    [Fact]
    public async Task Decide_CallerCancellationPropagates()
    {
        using var handler = new CapturingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Reply(ValidAnswer);
        });
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(http).DecideAsync(Request(), cts.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Decide_UsesShorterOfCallerBudgetAndConfiguredDeadline()
    {
        using var handler = new CapturingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Reply(ValidAnswer);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<JevDecisionException>(
            () => Client(http).DecideAsync(Request(TimeSpan.FromMilliseconds(100)), CancellationToken.None));
        Assert.Equal(JevFailureCategory.Timeout, error.Category);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Decide_RejectsUnevaluatedCatalogBeforeHttp()
    {
        using var handler = new CapturingHandler((_, _) => Task.FromResult(Reply(ValidAnswer)));
        using var http = new HttpClient(handler);
        var request = Request() with
        {
            Catalog = Request().Catalog with { CatalogVersion = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
        };
        var error = await Assert.ThrowsAsync<JevDecisionException>(
            () => Client(http).DecideAsync(request, CancellationToken.None));
        Assert.Equal(JevFailureCategory.InvalidRequest, error.Category);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Decide_ConfiguredTimeoutAlsoBoundsRequest()
    {
        using var handler = new CapturingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Reply(ValidAnswer);
        });
        using var http = new HttpClient(handler);
        var client = Client(http, timeoutSeconds: 1);
        var error = await Assert.ThrowsAsync<JevDecisionException>(
            () => client.DecideAsync(Request(TimeSpan.FromSeconds(10)), CancellationToken.None));
        Assert.Equal(JevFailureCategory.Timeout, error.Category);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Decide_RejectsOversizedResponse()
    {
        using var handler = new CapturingHandler((_, _) =>
            Task.FromResult(Reply(new string('x', 1_048_577))));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<JevDecisionException>(
            () => Client(http).DecideAsync(Request(), CancellationToken.None));
        Assert.Equal(JevFailureCategory.ProtocolError, error.Category);
    }

    [Fact]
    public async Task Decide_NetworkFailureIsSafeAndDoesNotRetry()
    {
        using var handler = new CapturingHandler((_, _) =>
            throw new HttpRequestException("private provider details"));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<JevDecisionException>(
            () => Client(http).DecideAsync(Request(), CancellationToken.None));
        Assert.Equal(JevFailureCategory.ProviderUnavailable, error.Category);
        Assert.DoesNotContain("private", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    private const string ValidAnswer = """
        {
          "model":"typesafe/jev-1.13-20260917",
          "answers":{
            "intent":{"type":"choice","choice":"service_search","confidence":0.9,
              "probabilities":{"service_search":0.9,"not_applicable":0.1}},
            "category":{"type":"choice","choice":"carpentry","confidence":0.75,
              "probabilities":{"carpentry":0.8,"decision:needs_clarification":0.1,"decision:no_match":0.1}}
          },
          "usage":{"input_tokens":12,"output_tokens":5,"cost":0.0001}
        }
        """;

    private static JevDecisionRequest Request(TimeSpan? remaining = null)
    {
        var categories = ImmutableArray.Create(
            new CategoryCatalogItem("carpentry", "Carpentry", "Wood repair", ["woodworking"]));
        var catalog = new CategoryCatalogSnapshot(
            1, CatalogVersion, categories,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                catalogVersion = CatalogVersion,
                categories
            }), DateTimeOffset.UtcNow);
        return new JevDecisionRequest(
            "Please repair my table", ["I meant wood"], catalog,
            remaining ?? TimeSpan.FromSeconds(10));
    }

    private static JevDecisionClient Client(HttpClient http, int timeoutSeconds = 3) => new(
        http,
        Options.Create(new CategoryResolutionOptions
        {
            Jev = new JevOptions
            {
                Endpoint = Endpoint,
                Model = "typesafe/jev-1.13",
                TimeoutSeconds = timeoutSeconds,
                Policy = new JevDecisionPolicyOptions
                {
                    EvaluatedCatalogVersion = CatalogVersion,
                    DistributionTolerance = 0.001
                }
            }
        }),
        Options.Create(new OpenRouterOptions { ApiKey = "fake-key" }));

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public AuthenticationHeaderValue? Auth { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Method = request.Method;
            Auth = request.Headers.Authorization;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return await reply(request, cancellationToken);
        }
    }
}
