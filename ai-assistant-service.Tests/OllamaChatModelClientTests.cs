using System.Net;
using System.Text;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ai_assistant_service.Tests;

public class OllamaChatModelClientTests
{
    [Fact]
    public async Task Chat_SendsNativeWireFormatAndReadsAnswerAndUsage()
    {
        using var handler = new CapturingHandler(_ => Reply("""
            {"model":"resolved-model","message":{"role":"assistant","content":"Hello"},
             "done":true,"prompt_eval_count":12,"eval_count":4}
            """));
        using var http = Http(handler);
        var request = Request(tools: false);

        var response = await new OllamaChatModelClient(http).ChatAsync(request, CancellationToken.None);

        using var sent = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var root = sent.RootElement;
        Assert.Equal(new Uri("http://ollama-test:11434/api/chat"), handler.LastUri);
        Assert.Equal("qwen3:8b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(128, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.Equal("hello", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Hello", response.Message.Content);
        Assert.Equal("resolved-model", response.ResolvedModel);
        Assert.Equal("qwen3:8b", request.Model);
        Assert.Equal(16, response.Usage!.TotalTokens);
    }

    [Fact]
    public async Task NativeCalls_RetainTypedArgumentsAndIdentityAcrossFollowUp()
    {
        using var handler = new CapturingHandler(index => Reply(index == 1 ? """
            {"message":{"role":"assistant","tool_calls":[
              {"id":"provider-call","function":{"name":"search","arguments":{
                "maxRate":25,"available":true,"location":null,"tags":["a"],"nested":{"x":2}}}},
              {"function":{"name":"bookings","arguments":{}}},
              {"function":{"name":"search","arguments":{}}}
            ]}}
            """ : """{"message":{"role":"assistant","content":"Finished"}}"""));
        using var http = Http(handler);
        var adapter = new OllamaChatModelClient(http);
        var request = Request();

        var response = await adapter.ChatAsync(request, CancellationToken.None);
        var calls = response.Message.ToolCalls!;
        Assert.Equal(3, calls.Select(call => call.Id).Distinct().Count());
        Assert.Equal("provider-call", calls[0].Id);
        Assert.All(calls, call => Assert.False(string.IsNullOrWhiteSpace(call.Id)));
        Assert.Equal(25, calls[0].Arguments["maxRate"].GetInt32());
        Assert.True(calls[0].Arguments["available"].GetBoolean());
        Assert.Equal(JsonValueKind.Null, calls[0].Arguments["location"].ValueKind);
        Assert.Equal("a", calls[0].Arguments["tags"][0].GetString());
        Assert.Equal(2, calls[0].Arguments["nested"].GetProperty("x").GetInt32());

        var followUp = new ChatModelRequest(request.Model,
            [.. request.Messages, response.Message,
             new ChatModelMessage("tool", "search-result", toolCallId: calls[0].Id),
             new ChatModelMessage("tool", "bookings-result", toolCallId: calls[1].Id),
             new ChatModelMessage("tool", "second-search", toolCallId: calls[2].Id)],
            request.Tools, request.MaxOutputTokens);
        await adapter.ChatAsync(followUp, CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.Bodies[1]);
        var messages = sent.RootElement.GetProperty("messages");
        Assert.Equal("search", messages[2].GetProperty("tool_name").GetString());
        Assert.Equal("bookings", messages[3].GetProperty("tool_name").GetString());
        Assert.Equal("second-search", messages[4].GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Object,
            messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").ValueKind);
        Assert.False(messages[2].TryGetProperty("tool_call_id", out _));
        foreach (var body in handler.Bodies)
        {
            using var document = JsonDocument.Parse(body);
            var tools = document.RootElement.GetProperty("tools");
            Assert.Equal(2, tools.GetArrayLength());
            Assert.Equal("function", tools[0].GetProperty("type").GetString());
            Assert.Equal("object", tools[0].GetProperty("function").GetProperty("parameters")
                .GetProperty("type").GetString());
        }
    }

    [Theory]
    [InlineData("""{"name":"search","parameters":{"maxRate":25}}""")]
    [InlineData("```json\n{\"name\":\"search\",\"arguments\":{\"maxRate\":25}}\n```")]
    [InlineData("I'll search now: {\"name\":\"search\",\"parameters\":{\"maxRate\":25}}")]
    public async Task InlineCalls_AreRecoveredWithIdsAndNoJsonAnswer(string content)
    {
        using var handler = new CapturingHandler(_ => Reply(AssistantContent(content)));
        using var http = Http(handler);
        var adapter = new OllamaChatModelClient(http);
        var first = await adapter.ChatAsync(Request(), CancellationToken.None);
        var second = await adapter.ChatAsync(Request(), CancellationToken.None);

        var call = Assert.Single(first.Message.ToolCalls!);
        Assert.Null(first.Message.Content);
        Assert.Equal("search", call.Name);
        Assert.Equal(25, call.Arguments["maxRate"].GetInt32());
        Assert.NotEqual(call.Id, Assert.Single(second.Message.ToolCalls!).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"message":{"role":"user","content":"wrong role"}}""")]
    [InlineData("""{"message":{"role":"assistant","content":" "}}""")]
    [InlineData("""{"message":{"role":"assistant","content":123}}""")]
    [InlineData("""{"done":false,"message":{"role":"assistant","content":"partial"}}""")]
    [InlineData("""{"done_reason":"length","message":{"role":"assistant","tool_calls":[{"function":{"name":"search","arguments":{}}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":{}}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"arguments":{}}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"name":"unregistered","arguments":{}}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"name":"search","arguments":"{}"}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"name":"search","arguments":[]}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"name":"search","arguments":null}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"name":"search","arguments":42}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"name":"search","parameters":{}}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"name":"search"}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"name":"search","arguments":{"x":1,"x":2}}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"id":"same","function":{"name":"search","arguments":{}}},{"id":"same","function":{"name":"search","arguments":{}}}]}}""")]
    public async Task InvalidOutput_FailsWholeResponseWithoutRetry(string body)
    {
        using var handler = new CapturingHandler(_ => Reply(body));
        using var http = Http(handler);
        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            new OllamaChatModelClient(http).ChatAsync(Request(), CancellationToken.None));

        Assert.Equal(ChatModelFailureCategory.ProtocolError, exception.Category);
        Assert.Single(handler.Bodies);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("""{"name":"unregistered","parameters":{}}""")]
    [InlineData("""{"name":"search","parameters":[]}""")]
    [InlineData("""{"name":"search","parameters":"{}"}""")]
    [InlineData("""{"name":"search"}""")]
    [InlineData("""{"name":"search","parameters":""")]
    [InlineData("""{"name":"search","parameters":broken}""")]
    public async Task InvalidInlineCalls_AreNotConvertedToEmptyArguments(string content)
    {
        using var handler = new CapturingHandler(_ => Reply(AssistantContent(content)));
        using var http = Http(handler);
        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            new OllamaChatModelClient(http).ChatAsync(Request(), CancellationToken.None));
        Assert.Equal(ChatModelFailureCategory.ProtocolError, exception.Category);
    }

    [Theory]
    [InlineData("Use {braces} in the description.")]
    [InlineData("""Here are the results: {"count":0}""")]
    public async Task OrdinaryContent_IsNotInterpretedAsAnInlineCall(string content)
    {
        using var handler = new CapturingHandler(_ => Reply(AssistantContent(content)));
        using var http = Http(handler);
        var response = await new OllamaChatModelClient(http).ChatAsync(Request(), CancellationToken.None);
        Assert.Equal(content, response.Message.Content);
        Assert.Null(response.Message.ToolCalls);
    }

    [Theory]
    [InlineData("""{"message":{"role":"assistant","tool_calls":[{"function":{"name":"search","arguments":{}}}]}}""")]
    [InlineData("""{"message":{"role":"assistant","content":"{\"name\":\"search\",\"parameters\":{}}"}}""")]
    public async Task CallsWithoutRequestToolDefinitions_AreRejected(string body)
    {
        using var handler = new CapturingHandler(_ => Reply(body));
        using var http = Http(handler);
        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            new OllamaChatModelClient(http).ChatAsync(Request(tools: false), CancellationToken.None));
        Assert.Equal(ChatModelFailureCategory.ProtocolError, exception.Category);
    }

    [Fact]
    public async Task CallIdCollidingWithHistory_IsRejected()
    {
        using var handler = new CapturingHandler(_ => Reply("""
            {"message":{"role":"assistant","tool_calls":[
              {"id":"seed_example_0_call_0","function":{"name":"search","arguments":{}}}
            ]}}
            """));
        using var http = Http(handler);
        var request = Request();
        var seed = new ChatModelToolCall("seed_example_0_call_0", "search",
            JsonSerializer.SerializeToElement(new { }));
        var withHistory = new ChatModelRequest(request.Model,
            [new ChatModelMessage("assistant", null, [seed]),
             new ChatModelMessage("tool", "[]", toolCallId: seed.Id),
             .. request.Messages], request.Tools, request.MaxOutputTokens);

        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            new OllamaChatModelClient(http).ChatAsync(withHistory, CancellationToken.None));
        Assert.Equal(ChatModelFailureCategory.ProtocolError, exception.Category);
    }

    [Fact]
    public async Task ToolResultWithoutOrigin_FailsBeforeHttpInsteadOfGuessingItsName()
    {
        using var handler = new CapturingHandler(_ => Reply("{}"));
        using var http = Http(handler);
        var request = new ChatModelRequest("model",
            [new ChatModelMessage("tool", "result", toolCallId: "unknown")], null, 128);
        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            new OllamaChatModelClient(http).ChatAsync(request, CancellationToken.None));
        Assert.Equal(ChatModelFailureCategory.InvalidRequest, exception.Category);
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(400, ChatModelFailureCategory.InvalidRequest)]
    [InlineData(401, ChatModelFailureCategory.Authentication)]
    [InlineData(403, ChatModelFailureCategory.PolicyRejected)]
    [InlineData(404, ChatModelFailureCategory.LocalModelUnavailable)]
    [InlineData(408, ChatModelFailureCategory.Timeout)]
    [InlineData(429, ChatModelFailureCategory.RateLimited)]
    [InlineData(500, ChatModelFailureCategory.ProviderUnavailable)]
    [InlineData(502, ChatModelFailureCategory.ProviderUnavailable)]
    [InlineData(503, ChatModelFailureCategory.ProviderUnavailable)]
    [InlineData(504, ChatModelFailureCategory.Timeout)]
    [InlineData(418, ChatModelFailureCategory.UnknownProviderError)]
    public async Task HttpFailures_AreTypedWithoutRawBodiesOrRetries(
        int status, ChatModelFailureCategory expected)
    {
        using var handler = new CapturingHandler(_ => Reply("sentinel-private-payload", status));
        using var http = Http(handler);
        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            new OllamaChatModelClient(http).ChatAsync(Request(), CancellationToken.None));

        Assert.Equal(expected, exception.Category);
        Assert.Equal(status, exception.Metadata.HttpStatusCode);
        Assert.Equal("qwen3:8b", exception.Metadata.RequestedModel);
        Assert.DoesNotContain("sentinel", exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task ErrorEnvelope_IsNeverReturnedAsSuccessfulAssistantContent()
    {
        using var handler = new CapturingHandler(_ => Reply(
            """{"error":"sentinel-secret","message":{"role":"assistant","content":"ignore error"}}"""));
        using var http = Http(handler);
        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            new OllamaChatModelClient(http).ChatAsync(Request(), CancellationToken.None));
        Assert.Equal(ChatModelFailureCategory.UnknownProviderError, exception.Category);
        Assert.DoesNotContain("sentinel", exception.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailures_AreSafeAndNeverRetried(bool timeout)
    {
        using var handler = new CapturingHandler(_ => timeout
            ? throw new TaskCanceledException("sentinel-private-payload")
            : throw new HttpRequestException("sentinel-private-payload"));
        using var http = Http(handler);
        var exception = await Assert.ThrowsAsync<ChatModelException>(() =>
            new OllamaChatModelClient(http).ChatAsync(Request(), CancellationToken.None));
        Assert.Equal(timeout ? ChatModelFailureCategory.Timeout : ChatModelFailureCategory.ProviderUnavailable,
            exception.Category);
        Assert.DoesNotContain("sentinel", exception.ToString());
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesDuringHttpAndBeforeHttp()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CapturingHandler(_ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Reply("{}");
        });
        using var http = Http(handler);
        var adapter = new OllamaChatModelClient(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.ChatAsync(Request(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.ChatAsync(Request(), cancellation.Token));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task RegisteredAdapter_UsesEachResolvedTaskProfileWithoutChangingHttpDefaults()
    {
        using var handler = new CapturingHandler(_ => Reply(AssistantContent("ok")));
        var values = new Dictionary<string, string?> { ["Ollama:BaseUrl"] = "http://ollama-test:11434" };
        foreach (var id in new[] { "chat", "synthetic" })
        {
            values[$"AiModels:Tasks:{id}:Provider"] = "Ollama";
            values[$"AiModels:Tasks:{id}:Model"] = $"{id}-model";
            values[$"AiModels:Tasks:{id}:TimeoutSeconds"] = "60";
            values[$"AiModels:Tasks:{id}:RequestTimeoutSeconds"] = "300";
            values[$"AiModels:Tasks:{id}:MaxOutputTokens"] = id == "chat" ? "128" : "32";
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiModelRouting(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddHttpClient<OllamaChatModelClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<ITaskChatModelResolver>();

        foreach (var id in new[] { "chat", "synthetic", "chat" })
        {
            await resolver.Resolve(id).ChatAsync(Request().Messages, null, CancellationToken.None);
        }

        Assert.Equal(3, handler.Bodies.Count);
        for (var index = 0; index < handler.Bodies.Count; index++)
        {
            using var sent = JsonDocument.Parse(handler.Bodies[index]);
            Assert.Equal(index == 1 ? "synthetic-model" : "chat-model",
                sent.RootElement.GetProperty("model").GetString());
            Assert.Equal(index == 1 ? 32 : 128,
                sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        }
        using var configuredHttp = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(OllamaChatModelClient));
        Assert.Equal(Timeout.InfiniteTimeSpan, configuredHttp.Timeout);
    }

    private static ChatModelRequest Request(bool tools = true) => new(
        "qwen3:8b", [new ChatModelMessage("user", "hello")],
        tools ? [
            new ChatModelTool("search", "Search task masters", JsonSerializer.SerializeToElement(new { type = "object" })),
            new ChatModelTool("bookings", "Get bookings", JsonSerializer.SerializeToElement(new { type = "object" }))
        ] : null, 128);

    private static string AssistantContent(string content) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content } });

    private static HttpResponseMessage Reply(string body, int status = 200) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpClient Http(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://ollama-test:11434") };

    private sealed class CapturingHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return respond(Bodies.Count);
        }
    }
}
