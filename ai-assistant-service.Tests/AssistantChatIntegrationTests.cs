using System.Net;
using System.Text;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ai_assistant_service.Tests;

public class AssistantChatIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredSeedsAndLiveCalls_RoundTripThroughResolverAndRealAdapter(bool inlineCall)
    {
        using var handler = new ModelHandler(inlineCall);
        var tool = new SearchTool();
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json")
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ollama:BaseUrl"] = "http://ollama-test:11434"
                }))
            .ConfigureServices((context, services) =>
            {
                services.AddAiModelRouting(context.Configuration);
                services.AddHttpClient<OllamaChatModelClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddSingleton<IToolDefinition>(tool);
                services.AddSingleton<ToolRegistry>();
                services.AddScoped<IAiAssistantService, AiAssistantService>();
            })
            .Build();
        await host.StartAsync();
        using var scope = host.Services.CreateScope();
        var assistant = scope.ServiceProvider.GetRequiredService<IAiAssistantService>();
        var executionContext = new ToolExecutionContext("alice", ["calendar.read"], "integration");
        var request = JsonSerializer.Deserialize<ChatRequest>("""
            {"message":"Find a landscaper","taskId":"synthetic","provider":"OpenRouter","model":"untrusted"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var response = await assistant.ChatAsync(request, executionContext, CancellationToken.None);

        Assert.Equal("Found Alice.", response.Answer);
        Assert.Equal("qwen3:8b", response.Model);
        Assert.Equal(["search_task_masters"], response.Sources);
        Assert.Equal("Alice", Assert.Single(response.Mentions).Name);
        Assert.Same(executionContext, tool.Context);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(2, handler.Bodies.Count);
        using var initial = JsonDocument.Parse(handler.Bodies[0]);
        var messages = initial.RootElement.GetProperty("messages");
        Assert.Equal(6, messages.GetArrayLength());
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user"],
            messages.EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("landscaping", messages[2].GetProperty("tool_calls")[0]
            .GetProperty("function").GetProperty("arguments").GetProperty("category").GetString());
        Assert.Equal("search_task_masters", messages[3].GetProperty("tool_name").GetString());

        using var next = JsonDocument.Parse(handler.Bodies[1]);
        var followUp = next.RootElement.GetProperty("messages");
        Assert.Equal(8, followUp.GetArrayLength());
        Assert.Equal("assistant", followUp[6].GetProperty("role").GetString());
        Assert.Equal("search_task_masters", followUp[7].GetProperty("tool_name").GetString());
        Assert.Contains("Alice", followUp[7].GetProperty("content").GetString());
        foreach (var body in handler.Bodies)
        {
            using var parsed = JsonDocument.Parse(body);
            Assert.Equal("qwen3:8b", parsed.RootElement.GetProperty("model").GetString());
            Assert.Equal(1024, parsed.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
            Assert.Single(parsed.RootElement.GetProperty("tools").EnumerateArray());
        }
        await host.StopAsync();
    }

    [Fact]
    public async Task OpenRouterRetryAfterToolRound_DoesNotReexecuteCompletedTool()
    {
        using var handler = new OpenRouterRetryHandler();
        var tool = new SearchTool();
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json")
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiModels:Tasks:chat:Provider"] = "OpenRouter",
                    ["AiModels:Tasks:chat:Model"] = "vendor/tool-model",
                    ["AiModels:Tasks:chat:TimeoutSeconds"] = "10",
                    ["AiModels:Tasks:chat:RequestTimeoutSeconds"] = "30",
                    ["AiModels:Tasks:chat:MaxOutputTokens"] = "128",
                    ["OpenRouter:BaseUrl"] = "https://openrouter.test/api/v1",
                    ["OpenRouter:ApiKey"] = "test-api-key",
                    ["OpenRouter:MaxRetries"] = "1"
                }))
            .ConfigureServices((context, services) =>
            {
                services.AddAiModelRouting(context.Configuration);
                services.AddHttpClient<OpenRouterChatModelClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddSingleton<IToolDefinition>(tool);
                services.AddSingleton<ToolRegistry>();
                services.AddScoped<IAiAssistantService, AiAssistantService>();
            })
            .Build();
        await host.StartAsync();
        using var scope = host.Services.CreateScope();
        var assistant = scope.ServiceProvider.GetRequiredService<IAiAssistantService>();

        var response = await assistant.ChatAsync(
            new ChatRequest { Message = "Find a landscaper" },
            new ToolExecutionContext("alice", ["calendar.read"], "integration"),
            CancellationToken.None);

        Assert.Equal("Found Alice.", response.Answer);
        Assert.Equal("vendor/tool-model", response.Model);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[1], handler.Bodies[2]);
        using var retried = JsonDocument.Parse(handler.Bodies[2]);
        var messages = retried.RootElement.GetProperty("messages");
        Assert.Equal(
            "live-call",
            messages[messages.GetArrayLength() - 1].GetProperty("tool_call_id").GetString());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("authentication")]
    [InlineData("invalid-arguments")]
    [InlineData("partial-progress")]
    public async Task OpenRouterSeedsAndLiveRounds_PreserveIdentityRoutingAndRequestLocalMetadata(string outcome)
    {
        using var handler = new OpenRouterConversationHandler(outcome);
        var tool = new SearchTool();
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json")
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiModels:Tasks:chat:Provider"] = "OpenRouter",
                    ["AiModels:Tasks:chat:Model"] = "vendor/requested-model",
                    ["OpenRouter:BaseUrl"] = "https://openrouter.test/api/v1",
                    ["OpenRouter:ApiKey"] = "fake-integration-key",
                    ["OpenRouter:MaxRetries"] = "0"
                }))
            .ConfigureServices((context, services) =>
            {
                services.AddAiModelRouting(context.Configuration);
                services.AddHttpClient<OpenRouterChatModelClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddSingleton<IToolDefinition>(tool);
                services.AddSingleton<ToolRegistry>();
                services.AddScoped<IAiAssistantService, AiAssistantService>();
            })
            .Build();
        await host.StartAsync();
        using var scope = host.Services.CreateScope();
        var request = JsonSerializer.Deserialize<ChatRequest>("""
            {"message":"Find a landscaper","taskId":"synthetic","provider":"Ollama",
             "model":"untrusted","history":[
               {"role":"user","content":"history-user-sentinel"},
               {"role":"assistant","content":"history-answer-sentinel"}]}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var response = await scope.ServiceProvider.GetRequiredService<IAiAssistantService>()
            .ChatAsync(request, new ToolExecutionContext("alice", [], "integration"), CancellationToken.None);

        Assert.Equal("vendor/requested-model", response.Model);
        Assert.Equal(outcome is "authentication" or "invalid-arguments" ? 0 : 1, tool.Calls);
        Assert.Equal(outcome is "authentication" or "invalid-arguments" ? 1 : 2, handler.Bodies.Count);
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        var messages = first.RootElement.GetProperty("messages");
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user", "assistant", "user"],
            messages.EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        var seededCall = messages[2].GetProperty("tool_calls")[0];
        var seedId = seededCall.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(seedId));
        Assert.NotEqual("live-search", seedId);
        Assert.Equal(seedId, messages[3].GetProperty("tool_call_id").GetString());
        using var seedArguments = JsonDocument.Parse(seededCall.GetProperty("function").GetProperty("arguments").GetString()!);
        Assert.Equal("landscaping", seedArguments.RootElement.GetProperty("category").GetString());
        Assert.Equal("history-user-sentinel", messages[5].GetProperty("content").GetString());
        Assert.Equal("history-answer-sentinel", messages[6].GetProperty("content").GetString());
        foreach (var body in handler.Bodies)
        {
            using var parsed = JsonDocument.Parse(body);
            Assert.Equal("vendor/requested-model", parsed.RootElement.GetProperty("model").GetString());
            Assert.Equal(1024, parsed.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Single(parsed.RootElement.GetProperty("tools").EnumerateArray());
        }
        if (handler.Bodies.Count == 2)
        {
            using var next = JsonDocument.Parse(handler.Bodies[1]);
            var followUp = next.RootElement.GetProperty("messages");
            Assert.Equal("live-search", followUp[8].GetProperty("tool_calls")[0].GetProperty("id").GetString());
            Assert.Equal("live-search", followUp[9].GetProperty("tool_call_id").GetString());
            Assert.Contains("tm-live", followUp[9].GetProperty("content").GetString());
        }
        if (outcome == "success")
        {
            Assert.Equal("Found Alice.", response.Answer);
            Assert.Equal(["search_task_masters"], response.Sources);
            Assert.Equal("Alice", Assert.Single(response.Mentions).Name);
        }
        else
        {
            Assert.Empty(response.Sources);
            Assert.Empty(response.Mentions);
            Assert.DoesNotContain("sentinel", response.Answer);
            Assert.DoesNotContain("fake-integration-key", response.Answer);
            Assert.DoesNotContain("Found Alice.", response.Answer);
        }
        await host.StopAsync();
    }

    private sealed class OpenRouterConversationHandler(string outcome) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://openrouter.test/api/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fake-integration-key", request.Headers.Authorization?.Parameter);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = outcome == "authentication" || (outcome == "partial-progress" && Bodies.Count == 2)
                ? """{"error":{"code":401,"message":"private-provider-sentinel"}}"""
                : Bodies.Count == 2
                    ? """{"model":"vendor/resolved-model","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Found Alice."}}]}"""
                    : outcome == "invalid-arguments"
                        ? """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"live-search","type":"function","function":{"name":"search_task_masters","arguments":"[]"}}]}}]}"""
                        : """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"live-search","type":"function","function":{"name":"search_task_masters","arguments":"{\"category\":\"landscaping\"}"}}]}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ModelHandler(bool inlineCall) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = Bodies.Count > 1
                ? """{"model":"resolved-model","message":{"role":"assistant","content":"Found Alice."}}"""
                : inlineCall
                    ? """{"message":{"role":"assistant","content":"{\"name\":\"search_task_masters\",\"parameters\":{\"category\":\"landscaping\"}}"}}"""
                    : """{"message":{"role":"assistant","tool_calls":[{"function":{"name":"search_task_masters","arguments":{"category":"landscaping"}}}]}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class OpenRouterRetryHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Bodies.Count == 1)
            {
                return Reply("""
                    {"choices":[{"message":{"role":"assistant","tool_calls":[{
                      "id":"live-call","type":"function","function":{
                        "name":"search_task_masters",
                        "arguments":"{\"category\":\"landscaping\"}"
                      }
                    }]}}]}
                    """);
            }

            if (Bodies.Count == 2)
            {
                var unavailable = Reply(
                    """{"error":{"code":503,"message":"temporarily unavailable"}}""",
                    HttpStatusCode.ServiceUnavailable);
                unavailable.Headers.TryAddWithoutValidation("Retry-After", "0");
                return unavailable;
            }

            return Reply(
                """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Found Alice."}}]}""");
        }

        private static HttpResponseMessage Reply(
            string body,
            HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    private sealed class SearchTool : IToolDefinition
    {
        public string Name => "search_task_masters";
        public string Description => "Search TaskMasters.";
        public object ParametersSchema => new { type = "object" };
        public int Calls { get; private set; }
        public ToolExecutionContext? Context { get; private set; }

        public Task<string> ExecuteAsync(ToolExecutionContext executionContext,
            IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
        {
            Calls++;
            Context = executionContext;
            Assert.Equal("landscaping", arguments["category"]);
            return Task.FromResult("""[{"id":"tm-live","name":"Alice"}]""");
        }
    }
}
