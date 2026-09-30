using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ai_assistant_service.Tests;

public class OpenRouterChatModelClientTests
{
    [Theory]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("https://openrouter.ai/api/v1/")]
    public async Task Chat_SendsConfiguredWireFormatHeadersAndReadsDiagnostics(string baseUrl)
    {
        using var handler = new CapturingHandler(_ => Reply("""
            {
              "id":"generation-1",
              "model":"provider/resolved-model",
              "choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Hello"}}],
              "usage":{"prompt_tokens":12,"completion_tokens":4,"total_tokens":16}
            }
            """));
        using var http = new HttpClient(handler);
        var client = Client(http, baseUrl, referer: "https://taskmaster.example");

        var response = await client.ChatAsync(Request(), CancellationToken.None);

        Assert.Equal(
            new Uri("https://openrouter.ai/api/v1/chat/completions"),
            handler.LastUri);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("test-api-key", handler.Authorization?.Parameter);
        Assert.Equal("https://taskmaster.example", handler.Headers["HTTP-Referer"]);
        Assert.Equal("TaskMaster Hub", handler.Headers["X-OpenRouter-Title"]);
        Assert.Equal("hidden", handler.Headers["X-OpenRouter-App-Visibility"]);

        using var sent = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var root = sent.RootElement;
        Assert.Equal("vendor/requested-model", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(128, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("think", out _));
        Assert.True(root.GetProperty("provider").GetProperty("allow_fallbacks").GetBoolean());
        Assert.True(root.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        Assert.Equal("deny", root.GetProperty("provider").GetProperty("data_collection").GetString());
        Assert.Equal("function", root.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal(
            "object",
            root.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters")
                .GetProperty("type").GetString());

        Assert.Equal("Hello", response.Message.Content);
        Assert.Equal("generation-1", response.ProviderRequestId);
        Assert.Equal("provider/resolved-model", response.ResolvedModel);
        Assert.Equal(16, response.Usage!.TotalTokens);
        Assert.Equal("vendor/requested-model", Request().Model);
    }

    [Fact]
    public async Task AttributionHeaders_AreOmittedWithoutReferer()
    {
        using var handler = new CapturingHandler(_ => Reply(Answer("ok")));
        using var http = new HttpClient(handler);

        await Client(http, "https://openrouter.ai/api/v1")
            .ChatAsync(Request(), CancellationToken.None);

        Assert.DoesNotContain("HTTP-Referer", handler.Headers.Keys);
        Assert.DoesNotContain("X-OpenRouter-Title", handler.Headers.Keys);
        Assert.DoesNotContain("X-OpenRouter-App-Visibility", handler.Headers.Keys);
    }

    [Fact]
    public async Task NativeCalls_RetainTypedArgumentsAndIdentityAcrossFollowUp()
    {
        using var handler = new CapturingHandler(index => Reply(index == 1 ? """
            {
              "id":"generation-1",
              "choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
                {"id":"call-search","type":"function","function":{
                  "name":"search","arguments":"{\"maxRate\":25,\"available\":true,\"tags\":[\"a\"],\"nested\":{\"x\":2}}"
                }},
                {"id":"call-bookings","type":"function","function":{
                  "name":"bookings","arguments":"{}"
                }}
              ]}}]
            }
            """ : Answer("Finished")));
        using var http = new HttpClient(handler);
        var adapter = Client(http, "https://openrouter.ai/api/v1");
        var request = Request();

        var first = await adapter.ChatAsync(request, CancellationToken.None);
        var calls = first.Message.ToolCalls!;
        Assert.Equal(["call-search", "call-bookings"], calls.Select(call => call.Id));
        Assert.Equal(25, calls[0].Arguments["maxRate"].GetInt32());
        Assert.True(calls[0].Arguments["available"].GetBoolean());
        Assert.Equal("a", calls[0].Arguments["tags"][0].GetString());
        Assert.Equal(2, calls[0].Arguments["nested"].GetProperty("x").GetInt32());

        var followUp = new ChatModelRequest(
            request.Model,
            [
                .. request.Messages,
                first.Message,
                new ChatModelMessage("tool", "search-result", toolCallId: calls[0].Id),
                new ChatModelMessage("tool", "bookings-result", toolCallId: calls[1].Id)
            ],
            request.Tools,
            request.MaxOutputTokens);
        await adapter.ChatAsync(followUp, CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.Bodies[1]);
        var messages = sent.RootElement.GetProperty("messages");
        var sentCalls = messages[1].GetProperty("tool_calls");
        Assert.Equal("call-search", sentCalls[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.String,
            sentCalls[0].GetProperty("function").GetProperty("arguments").ValueKind);
        using var sentArguments = JsonDocument.Parse(
            sentCalls[0].GetProperty("function").GetProperty("arguments").GetString()!);
        Assert.Equal(25, sentArguments.RootElement.GetProperty("maxRate").GetInt32());
        Assert.Equal("call-search", messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("call-bookings", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("search-result", messages[2].GetProperty("content").GetString());
        Assert.All(handler.Bodies, body =>
        {
            using var document = JsonDocument.Parse(body);
            Assert.Equal(2, document.RootElement.GetProperty("tools").GetArrayLength());
        });
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"user","content":"wrong"}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","content":" "}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":{}}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":[{}]}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"","function":{"name":"search","arguments":"{}"}}]}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":" call","function":{"name":"search","arguments":"{}"}}]}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call","function":{"name":"","arguments":"{}"}}]}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call","function":{"name":"search ","arguments":"{}"}}]}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call","function":{"name":"other","arguments":"{}"}}]}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call","function":{"name":"search","arguments":"[]"}}]}}]}""")]
    [InlineData("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call","function":{"name":"search","arguments":"broken"}}]}}]}""")]
    public async Task InvalidOutput_FailsWithoutChangingArguments(string body)
    {
        using var handler = new CapturingHandler(_ => Reply(body));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            Client(http, "https://openrouter.ai/api/v1")
                .ChatAsync(Request(), CancellationToken.None));

        Assert.Equal(ChatModelFailureCategory.ProtocolError, exception.Category);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(400, ChatModelFailureCategory.InvalidRequest)]
    [InlineData(401, ChatModelFailureCategory.Authentication)]
    [InlineData(402, ChatModelFailureCategory.CreditsExhausted)]
    [InlineData(403, ChatModelFailureCategory.PolicyRejected)]
    [InlineData(404, ChatModelFailureCategory.InvalidRequest)]
    [InlineData(408, ChatModelFailureCategory.Timeout)]
    [InlineData(429, ChatModelFailureCategory.RateLimited)]
    [InlineData(500, ChatModelFailureCategory.ProviderUnavailable)]
    [InlineData(502, ChatModelFailureCategory.ProviderUnavailable)]
    [InlineData(503, ChatModelFailureCategory.ProviderUnavailable)]
    [InlineData(504, ChatModelFailureCategory.Timeout)]
    [InlineData(418, ChatModelFailureCategory.UnknownProviderError)]
    public async Task HttpFailures_AreClassifiedWithoutRawProviderDetails(
        int status,
        ChatModelFailureCategory expected)
    {
        using var handler = new CapturingHandler(_ => Reply(
            Error(status, message: "sentinel-private-error"),
            status));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            Client(http, "https://openrouter.ai/api/v1", maxRetries: 0)
                .ChatAsync(Request(), CancellationToken.None));

        Assert.Equal(expected, exception.Category);
        Assert.Equal(status, exception.Metadata.HttpStatusCode);
        Assert.Equal(status, exception.Metadata.ProviderErrorCode);
        Assert.Equal("vendor/requested-model", exception.Metadata.RequestedModel);
        Assert.DoesNotContain("sentinel", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(400, ChatModelFailureCategory.InvalidRequest)]
    [InlineData(401, ChatModelFailureCategory.Authentication)]
    [InlineData(402, ChatModelFailureCategory.CreditsExhausted)]
    [InlineData(403, ChatModelFailureCategory.PolicyRejected)]
    [InlineData(404, ChatModelFailureCategory.InvalidRequest)]
    [InlineData(408, ChatModelFailureCategory.Timeout)]
    [InlineData(429, ChatModelFailureCategory.RateLimited)]
    [InlineData(500, ChatModelFailureCategory.ProviderUnavailable)]
    [InlineData(502, ChatModelFailureCategory.ProviderUnavailable)]
    [InlineData(503, ChatModelFailureCategory.ProviderUnavailable)]
    [InlineData(504, ChatModelFailureCategory.Timeout)]
    [InlineData(499, ChatModelFailureCategory.UnknownProviderError)]
    public async Task Http200ErrorEnvelope_UsesProviderCodeBeforeChoices(
        int providerCode,
        ChatModelFailureCategory expected)
    {
        var body = JsonSerializer.Serialize(new
        {
            id = "error-request-id",
            error = new { code = providerCode, message = "sentinel-private-error" },
            choices = new[]
            {
                new { message = new { role = "assistant", content = "must not succeed" } }
            }
        });
        using var handler = new CapturingHandler(_ => Reply(body));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            Client(http, "https://openrouter.ai/api/v1", maxRetries: 0)
                .ChatAsync(Request(), CancellationToken.None));

        Assert.Equal(expected, exception.Category);
        Assert.Equal(200, exception.Metadata.HttpStatusCode);
        Assert.Equal(providerCode, exception.Metadata.ProviderErrorCode);
        Assert.Equal("error-request-id", exception.Metadata.ProviderRequestId);
        Assert.DoesNotContain("sentinel", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Http200TransientErrorEnvelope_RetriesBeforeReadingChoices()
    {
        using var handler = new CapturingHandler(index => index == 1
            ? Reply(
                Error(503, message: "sentinel-provider-payload"),
                retryAfter: "0")
            : Reply(Answer("recovered")));
        using var http = new HttpClient(handler);

        var response = await Client(http, "https://openrouter.ai/api/v1")
            .ChatAsync(Request(), CancellationToken.None);

        Assert.Equal("recovered", response.Message.Content);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Theory]
    [InlineData("""{"error":null}""")]
    [InlineData("""{"error":"bad"}""")]
    [InlineData("""{"error":{}}""")]
    [InlineData("""{"error":{"code":"429"}}""")]
    [InlineData("""{"error":{"code":402,"metadata":[]}}""")]
    [InlineData("""{"error":{"code":402,"metadata":{"limit_source":5}}}""")]
    public async Task InvalidErrorEnvelope_IsProtocolFailure(string body)
    {
        using var handler = new CapturingHandler(_ => Reply(body));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            Client(http, "https://openrouter.ai/api/v1")
                .ChatAsync(Request(), CancellationToken.None));

        Assert.Equal(ChatModelFailureCategory.ProtocolError, exception.Category);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task RetryableHttpFailure_RetriesOnlyCurrentRequestOnce(int status)
    {
        using var handler = new CapturingHandler(index => index == 1
            ? Reply(Error(status), status, retryAfter: "0")
            : Reply(Answer("recovered")));
        using var http = new HttpClient(handler);

        var response = await Client(http, "https://openrouter.ai/api/v1")
            .ChatAsync(Request(), CancellationToken.None);

        Assert.Equal("recovered", response.Message.Content);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
    }

    [Fact]
    public async Task RetryableTransportFailure_UsesBoundedFallbackAndRetriesOnce()
    {
        using var handler = new CapturingHandler((index, _) =>
            index == 1
                ? Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("sentinel-private-error"))
                : Task.FromResult(Reply(Answer("recovered"))));
        using var http = new HttpClient(handler);

        var response = await Client(http, "https://openrouter.ai/api/v1")
            .ChatAsync(Request(), CancellationToken.None);

        Assert.Equal("recovered", response.Message.Content);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task ProviderTimeout_RetriesOnceWithoutBecomingCallerCancellation()
    {
        using var handler = new CapturingHandler((index, _) =>
            index == 1
                ? Task.FromException<HttpResponseMessage>(
                    new TaskCanceledException("sentinel-provider-timeout"))
                : Task.FromResult(Reply(Answer("recovered"))));
        using var http = new HttpClient(handler);

        var response = await Client(http, "https://openrouter.ai/api/v1")
            .ChatAsync(Request(), CancellationToken.None);

        Assert.Equal("recovered", response.Message.Content);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task RetryLogs_ExcludeApiKeyAndProviderPayload()
    {
        var logger = new RecordingLogger<OpenRouterChatModelClient>();
        using var handler = new CapturingHandler(index => index == 1
            ? Reply(
                Error(503, message: "sentinel-provider-payload"),
                503,
                retryAfter: "0")
            : Reply(Answer("recovered")));
        using var http = new HttpClient(handler);

        await Client(
                http,
                "https://openrouter.ai/api/v1",
                apiKey: "sentinel-api-key",
                logger: logger)
            .ChatAsync(Request(), CancellationToken.None);

        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("sentinel", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null, "openrouter_in_flight_budget")]
    [InlineData("0", null)]
    [InlineData("0", "unrelated_budget")]
    [InlineData("invalid", "openrouter_in_flight_budget")]
    public async Task TemporaryBudgetRetry_RequiresMatchingMetadataAndValidHeader(
        string? retryAfter,
        string? limitSource)
    {
        using var handler = new CapturingHandler(index => index == 1
            ? Reply(
                Error(402, limitSource: limitSource),
                402,
                retryAfter)
            : Reply(Answer("must not retry")));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            Client(http, "https://openrouter.ai/api/v1")
                .ChatAsync(Request(), CancellationToken.None));

        Assert.Equal(ChatModelFailureCategory.CreditsExhausted, exception.Category);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("date")]
    public async Task TemporaryBudgetRetry_HonorsDeltaSecondsAndHttpDate(string format)
    {
        var retryAfter = format == "date"
            ? DateTimeOffset.UtcNow.AddSeconds(-1).ToString("R", CultureInfo.InvariantCulture)
            : "0";
        using var handler = new CapturingHandler(index => index == 1
            ? Reply(
                Error(402, limitSource: "openrouter_in_flight_budget"),
                402,
                retryAfter)
            : Reply(Answer("recovered")));
        using var http = new HttpClient(handler);

        var response = await Client(http, "https://openrouter.ai/api/v1")
            .ChatAsync(Request(), CancellationToken.None);

        Assert.Equal("recovered", response.Message.Content);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task InvalidRetryAfter_OnRateLimitUsesFallbackRetry()
    {
        using var handler = new CapturingHandler(index => index == 1
            ? Reply(Error(429), 429, retryAfter: "invalid")
            : Reply(Answer("recovered")));
        using var http = new HttpClient(handler);

        var response = await Client(http, "https://openrouter.ai/api/v1")
            .ChatAsync(Request(), CancellationToken.None);

        Assert.Equal("recovered", response.Message.Content);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task RetryDelayBeyondRoundBudget_ReturnsFailureWithoutRetry()
    {
        using var handler = new CapturingHandler(_ =>
            Reply(Error(429), 429, retryAfter: "60"));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            Client(http, "https://openrouter.ai/api/v1")
                .ChatAsync(
                    Request(TimeSpan.FromMilliseconds(100)),
                    CancellationToken.None));

        Assert.Equal(ChatModelFailureCategory.RateLimited, exception.Category);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task RetryExhaustion_ReturnsSecondFailureWithoutThirdAttempt()
    {
        using var handler = new CapturingHandler(_ =>
            Reply(Error(503), 503, retryAfter: "0"));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            Client(http, "https://openrouter.ai/api/v1")
                .ChatAsync(Request(), CancellationToken.None));

        Assert.Equal(ChatModelFailureCategory.ProviderUnavailable, exception.Category);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task CallerCancellation_IsNotConvertedOrRetried()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CapturingHandler((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Reply(Answer("unreachable")));
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(http, "https://openrouter.ai/api/v1")
                .ChatAsync(Request(), cancellation.Token));

        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task CallerCancellationDuringRetryDelay_DoesNotSendAnotherRequest()
    {
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));
        using var handler = new CapturingHandler(_ =>
            Reply(Error(429), 429, retryAfter: "5"));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(http, "https://openrouter.ai/api/v1")
                .ChatAsync(Request(), cancellation.Token));

        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("length", ChatModelFailureCategory.ProtocolError)]
    [InlineData("max_tokens", ChatModelFailureCategory.ProtocolError)]
    [InlineData("content_filter", ChatModelFailureCategory.PolicyRejected)]
    public async Task UnsafeFinishReasons_FailBeforeToolExecution(
        string finishReason,
        ChatModelFailureCategory expected)
    {
        using var handler = new CapturingHandler(_ => Reply($$"""
            {
              "choices":[{
                "finish_reason":"{{finishReason}}",
                "message":{"role":"assistant","tool_calls":[{
                  "id":"call","type":"function",
                  "function":{"name":"search","arguments":"{}"}
                }]}
              }]
            }
            """));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            Client(http, "https://openrouter.ai/api/v1")
                .ChatAsync(Request(), CancellationToken.None));

        Assert.Equal(expected, exception.Category);
    }

    [Fact]
    public async Task RegisteredAdapter_UsesProfileModelsAndOutputLimits()
    {
        using var handler = new CapturingHandler(_ => Reply(Answer("ok")));
        var values = OpenRouterConfiguration();
        AddProfile(values, "chat", "chat-model", 128);
        AddProfile(values, "synthetic", "synthetic-model", 32);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiModelRouting(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddHttpClient<OpenRouterChatModelClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<ITaskChatModelResolver>();

        foreach (var id in new[] { "chat", "synthetic", "chat" })
        {
            await resolver.Resolve(id).ChatAsync(
                Request().Messages,
                Request().Tools,
                CancellationToken.None);
        }

        Assert.Equal(3, handler.Bodies.Count);
        for (var index = 0; index < handler.Bodies.Count; index++)
        {
            using var sent = JsonDocument.Parse(handler.Bodies[index]);
            Assert.Equal(
                index == 1 ? "synthetic-model" : "chat-model",
                sent.RootElement.GetProperty("model").GetString());
            Assert.Equal(
                index == 1 ? 32 : 128,
                sent.RootElement.GetProperty("max_tokens").GetInt32());
        }

        using var configuredHttp = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(OpenRouterChatModelClient));
        Assert.Equal(Timeout.InfiniteTimeSpan, configuredHttp.Timeout);
    }

    private static OpenRouterChatModelClient Client(
        HttpClient http,
        string baseUrl,
        string? referer = null,
        int maxRetries = 1,
        string apiKey = "test-api-key",
        ILogger<OpenRouterChatModelClient>? logger = null)
    {
        var options = Options.Create(new OpenRouterOptions
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            HttpReferer = referer,
            AppTitle = "TaskMaster Hub",
            AllowFallbacks = true,
            RequireParameters = true,
            DataCollection = "deny",
            MaxRetries = maxRetries
        });
        return new OpenRouterChatModelClient(
            http,
            options,
            logger ?? NullLogger<OpenRouterChatModelClient>.Instance);
    }

    private static ChatModelRequest Request(TimeSpan? roundTimeout = null) =>
        new(
            "vendor/requested-model",
            [new ChatModelMessage("user", "hello")],
            [
                new ChatModelTool(
                    "search",
                    "Search task masters",
                    JsonSerializer.SerializeToElement(new { type = "object" })),
                new ChatModelTool(
                    "bookings",
                    "Get bookings",
                    JsonSerializer.SerializeToElement(new { type = "object" }))
            ],
            128,
            roundTimeout ?? TimeSpan.FromSeconds(30));

    private static Dictionary<string, string?> OpenRouterConfiguration() =>
        new()
        {
            ["OpenRouter:BaseUrl"] = "https://openrouter.ai/api/v1",
            ["OpenRouter:ApiKey"] = "test-api-key",
            ["OpenRouter:AppTitle"] = "TaskMaster Hub",
            ["OpenRouter:AllowFallbacks"] = "true",
            ["OpenRouter:RequireParameters"] = "true",
            ["OpenRouter:DataCollection"] = "deny",
            ["OpenRouter:MaxRetries"] = "1"
        };

    private static void AddProfile(
        IDictionary<string, string?> values,
        string id,
        string model,
        int maxOutputTokens)
    {
        values[$"AiModels:Tasks:{id}:Provider"] = "OpenRouter";
        values[$"AiModels:Tasks:{id}:Model"] = model;
        values[$"AiModels:Tasks:{id}:TimeoutSeconds"] = "60";
        values[$"AiModels:Tasks:{id}:RequestTimeoutSeconds"] = "300";
        values[$"AiModels:Tasks:{id}:MaxOutputTokens"] = maxOutputTokens.ToString();
    }

    private static string Answer(string content) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content } }
            }
        });

    private static string Error(
        int code,
        string? limitSource = null,
        string message = "provider error") =>
        JsonSerializer.Serialize(new
        {
            id = "provider-request-id",
            error = new
            {
                code,
                message,
                metadata = limitSource is null
                    ? null
                    : new { limit_source = limitSource }
            }
        });

    private static HttpResponseMessage Reply(
        string body,
        int status = 200,
        string? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;

        public CapturingHandler(Func<int, HttpResponseMessage> respond)
            : this((index, _) => Task.FromResult(respond(index)))
        {
        }

        public CapturingHandler(
            Func<int, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public List<string> Bodies { get; } = [];
        public Uri? LastUri { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
        public Dictionary<string, string> Headers { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(",", header.Value);
            }

            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return await _respond(Bodies.Count, cancellationToken);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}
