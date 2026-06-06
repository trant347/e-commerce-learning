using System.Text.Json;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ai_assistant_service.Tests;

public class AiAssistantServiceTests
{
    private static IConfiguration BuildConfig(string model = "qwen3:8b") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:Model"]               = model,
                ["PromptOptions:SystemPrompt"] = "test system prompt"
            })
            .Build();

    [Fact]
    public async Task ChatAsync_NoToolCalls_ReturnsAssistantContent()
    {
        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(c => c.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<OllamaChatMessage>>(),
                It.IsAny<IReadOnlyList<object>?>(),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OllamaChatMessage { Role = "assistant", Content = "Hello!" });

        var svc = new AiAssistantService(
            BuildConfig(), ollama.Object, new ToolRegistry(Array.Empty<IToolDefinition>()),
            NullLogger<AiAssistantService>.Instance);

        var resp = await svc.ChatAsync(new ChatRequest { Message = "hi" }, CancellationToken.None);

        Assert.Equal("Hello!", resp.Answer);
        Assert.Equal("qwen3:8b", resp.Model);
        ollama.Verify(c => c.ChatAsync(
            "qwen3:8b",
            It.IsAny<IReadOnlyList<OllamaChatMessage>>(),
            It.IsAny<IReadOnlyList<object>?>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ChatAsync_WithToolCall_ExecutesToolThenReturnsFinalAnswer()
    {
        var fakeTool = new RecordingTool("search_task_masters", "[{\"id\":\"tm-1\",\"name\":\"Alice\"}]");
        var registry = new ToolRegistry(new IToolDefinition[] { fakeTool });

        using var argDoc = JsonDocument.Parse("{\"category\":\"Pet Care\"}");
        var args = argDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        var toolCallMsg = new OllamaChatMessage
        {
            Role = "assistant",
            ToolCalls = new List<OllamaToolCall>
            {
                new()
                {
                    Function = new OllamaToolCallFunction
                    {
                        Name = "search_task_masters",
                        Arguments = args
                    }
                }
            }
        };
        var finalMsg = new OllamaChatMessage { Role = "assistant", Content = "I found Alice." };

        var ollama = new Mock<IOllamaClient>();
        ollama.SetupSequence(c => c.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<OllamaChatMessage>>(),
                It.IsAny<IReadOnlyList<object>?>(),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(toolCallMsg)
              .ReturnsAsync(finalMsg);

        var svc = new AiAssistantService(
            BuildConfig(), ollama.Object, registry, NullLogger<AiAssistantService>.Instance);

        var resp = await svc.ChatAsync(
            new ChatRequest { Message = "find a pet sitter" }, CancellationToken.None);

        Assert.Equal("I found Alice.", resp.Answer);
        Assert.Equal(1, fakeTool.CallCount);
        Assert.Single(resp.Mentions);
        Assert.Equal("tm-1", resp.Mentions[0].Id);
        Assert.Equal("Alice", resp.Mentions[0].Name);
    }

    [Fact]
    public async Task ChatAsync_ParsesInlineToolCallFromContent_WhenModelEmitsRawJson()
    {
        // Some small models put the tool call into `content` as plain JSON
        // instead of using the native tool_calls field. Make sure we recover.
        var fakeTool = new RecordingTool("get_categories", "[\"Pet Care\"]");
        var registry = new ToolRegistry(new IToolDefinition[] { fakeTool });

        var inlineCall = new OllamaChatMessage
        {
            Role = "assistant",
            Content = "{\"name\": \"get_categories\", \"parameters\": {}}"
        };
        var finalMsg = new OllamaChatMessage { Role = "assistant", Content = "Pet Care is available." };

        var ollama = new Mock<IOllamaClient>();
        ollama.SetupSequence(c => c.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<OllamaChatMessage>>(),
                It.IsAny<IReadOnlyList<object>?>(),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(inlineCall)
              .ReturnsAsync(finalMsg);

        var svc = new AiAssistantService(
            BuildConfig(), ollama.Object, registry, NullLogger<AiAssistantService>.Instance);

        var resp = await svc.ChatAsync(
            new ChatRequest { Message = "what categories?" }, CancellationToken.None);

        Assert.Equal("Pet Care is available.", resp.Answer);
        Assert.Equal(1, fakeTool.CallCount);
    }

    [Fact]
    public async Task ChatAsync_SendsSystemPromptAsFirstMessage()
    {
        IReadOnlyList<OllamaChatMessage>? capturedMessages = null;

        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(c => c.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<OllamaChatMessage>>(),
                It.IsAny<IReadOnlyList<object>?>(),
                It.IsAny<CancellationToken>()))
              .Callback<string, IReadOnlyList<OllamaChatMessage>, IReadOnlyList<object>?, CancellationToken>(
                  (_, msgs, _, _) => capturedMessages = msgs.ToList())
              .ReturnsAsync(new OllamaChatMessage { Role = "assistant", Content = "ok" });

        var svc = new AiAssistantService(
            BuildConfig(), ollama.Object, new ToolRegistry(Array.Empty<IToolDefinition>()),
            NullLogger<AiAssistantService>.Instance);

        await svc.ChatAsync(new ChatRequest { Message = "hi" }, CancellationToken.None);

        Assert.NotNull(capturedMessages);
        Assert.Equal("system", capturedMessages![0].Role);
        Assert.Equal("test system prompt", capturedMessages[0].Content);
        Assert.Equal("user", capturedMessages[^1].Role);
        Assert.Equal("hi", capturedMessages[^1].Content);
    }

    [Fact]
    public async Task ChatAsync_HaltsAfterMaxRounds_AndReturnsFallbackMessage()
    {
        // Tool that always returns valid JSON, paired with a model that always
        // requests another tool call → exercises the MaxToolRounds (5) guard.
        var fakeTool = new RecordingTool("search_task_masters", "[]");
        var registry = new ToolRegistry(new IToolDefinition[] { fakeTool });

        using var argDoc = JsonDocument.Parse("{\"category\":\"Pet Care\"}");
        var args = argDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        OllamaChatMessage MakeToolCall() => new()
        {
            Role = "assistant",
            ToolCalls = new List<OllamaToolCall>
            {
                new()
                {
                    Function = new OllamaToolCallFunction
                    {
                        Name = "search_task_masters",
                        Arguments = new Dictionary<string, JsonElement>(args)
                    }
                }
            }
        };

        var ollama = new Mock<IOllamaClient>();
        ollama.Setup(c => c.ChatAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<OllamaChatMessage>>(),
                It.IsAny<IReadOnlyList<object>?>(),
                It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeToolCall);

        var svc = new AiAssistantService(
            BuildConfig(), ollama.Object, registry, NullLogger<AiAssistantService>.Instance);

        var resp = await svc.ChatAsync(
            new ChatRequest { Message = "loop forever" }, CancellationToken.None);

        Assert.Contains("wasn't able to find a complete answer", resp.Answer);
        Assert.Equal(5, fakeTool.CallCount);
        ollama.Verify(c => c.ChatAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<OllamaChatMessage>>(),
            It.IsAny<IReadOnlyList<object>?>(),
            It.IsAny<CancellationToken>()),
            Times.Exactly(5));
    }

    private sealed class RecordingTool : IToolDefinition
    {
        private readonly string _result;
        public RecordingTool(string name, string result) { Name = name; _result = result; }
        public string Name { get; }
        public string Description => "test tool";
        public object ParametersSchema => new { };
        public int CallCount { get; private set; }

        public Task<string> ExecuteAsync(
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }
}
