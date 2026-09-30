using System.Net;
using System.Text;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        string? referer = null) =>
        new(
            http,
            Options.Create(new OpenRouterOptions
            {
                BaseUrl = baseUrl,
                ApiKey = "test-api-key",
                HttpReferer = referer,
                AppTitle = "TaskMaster Hub",
                AllowFallbacks = true,
                RequireParameters = true,
                DataCollection = "deny",
                MaxRetries = 1
            }));

    private static ChatModelRequest Request() =>
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
            128);

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

    private static HttpResponseMessage Reply(string body, int status = 200) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class CapturingHandler(Func<int, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
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
            return respond(Bodies.Count);
        }
    }
}
