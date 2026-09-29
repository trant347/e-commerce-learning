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
