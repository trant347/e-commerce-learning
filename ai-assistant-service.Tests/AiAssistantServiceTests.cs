using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ai_assistant_service.Tests;

public class AiAssistantServiceTests
{
    [Fact]
    public async Task Chat_ResolvesChatOnceAndPreservesPromptHistoryToolsAndResultIds()
    {
        var first = new RecordingTool("search_task_masters", """[{"id":"tm-1","name":"Alice"}]""");
        var second = new RecordingTool("get_bookings", "[]");
        var client = new RecordingClient((round, _) => Task.FromResult(round == 1
            ? Calls(Call("live-search", first.Name), Call("live-bookings", second.Name))
            : Answer("I found Alice.", "provider-resolved-model")));
        var (service, resolver) = Service(client, [first, second]);
        var response = await service.ChatAsync(new ChatRequest
        {
            Message = "new question",
            History = [
                new() { Role = "user", Content = "discard this old message" },
                new() { Role = "user", Content = "recent question" },
                new() { Role = "assistant", Content = "recent answer" }
            ]
        }, Context, CancellationToken.None);

        resolver.Verify(r => r.Resolve("chat"), Times.Once);
        resolver.VerifyNoOtherCalls();
        Assert.Equal("I found Alice.", response.Answer);
        Assert.Equal("configured-model", response.Model);
        Assert.Equal(["search_task_masters", "get_bookings"], response.Sources);
        Assert.Equal("Alice", Assert.Single(response.Mentions).Name);
        Assert.Same(Context, first.LastContext);
        Assert.Same(Context, second.LastContext);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);

        var initial = client.Requests[0].Messages;
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "user", "assistant", "user"],
            initial.Select(m => m.Role));
        Assert.Equal("test system prompt", initial[0].Content);
        Assert.Equal("recent question", initial[5].Content);
        Assert.Equal("recent answer", initial[6].Content);
        Assert.Equal("new question", initial[7].Content);
        Assert.Equal(Assert.Single(initial[2].ToolCalls!).Id, initial[3].ToolCallId);
        var followUp = client.Requests[1].Messages;
        Assert.Equal(["live-search", "live-bookings"],
            followUp.Skip(initial.Count + 1).Select(m => m.ToolCallId));
        Assert.Equal(first.Result, followUp[^2].Content);
        Assert.Equal(second.Result, followUp[^1].Content);
        Assert.Same(client.Requests[0].Tools, client.Requests[1].Tools);
        Assert.Equal(2, client.Requests[0].Tools!.Count);
        Assert.All(client.Requests[0].Tools!, tool =>
            Assert.Equal(JsonValueKind.Object, tool.Parameters.ValueKind));
        Assert.Equal(client.Requests[0].Token, client.Requests[1].Token);
    }

    [Fact]
    public async Task NoLiveTools_DoesNotExposeRegisteredToolsOrSeedMetadata()
    {
        var tool = new RecordingTool("search_task_masters", "[]");
        var client = new RecordingClient((_, _) => Task.FromResult(Answer("Hello!")));
        var (service, _) = Service(client, [tool]);
        var response = await service.ChatAsync(new ChatRequest { Message = "hello" }, Context, CancellationToken.None);

        Assert.Equal("Hello!", response.Answer);
        Assert.Empty(response.Sources);
        Assert.Empty(response.Mentions);
        Assert.Equal(0, tool.CallCount);
    }

    [Fact]
    public async Task Grounding_UsesUserHistoryButNotSeededOrAssistantFilters()
    {
        var tool = new RecordingTool("search_task_masters", "[]");
        var client = new RecordingClient((round, _) => Task.FromResult(round == 1
            ? Calls(Call("call", tool.Name,
                """{"category":"education","location":"San Francisco","minRating":4.5}"""))
            : Answer("None found.")));
        var (service, _) = Service(client, [tool]);
        await service.ChatAsync(new ChatRequest
        {
            Message = "any English teachers?",
            History = [
                new() { Role = "user", Content = "in San Francisco" },
                new() { Role = "assistant", Content = "I'll use a minimum rating of 4.5." }
            ]
        }, Context, CancellationToken.None);

        Assert.Equal("education", tool.LastArgs!["category"]);
        Assert.Equal("San Francisco", tool.LastArgs["location"]);
        Assert.False(tool.LastArgs.ContainsKey("minRating"));
    }

    [Fact]
    public async Task FiveRoundGuard_DoesNotReturnASeededOrHistoricalAnswer()
    {
        var tool = new RecordingTool("search_task_masters", "[]");
        var client = new RecordingClient((round, _) =>
            Task.FromResult(Calls(Call($"call-{round}", tool.Name))));
        var (service, _) = Service(client, [tool]);
        var response = await service.ChatAsync(new ChatRequest
        {
            Message = "loop",
            History = [new() { Role = "assistant", Content = "historical answer" }]
        }, Context, CancellationToken.None);

        Assert.Contains("wasn't able to find a complete answer", response.Answer);
        Assert.Equal(5, tool.CallCount);
        Assert.Equal(5, client.Requests.Count);
        Assert.Empty(response.Mentions);
    }

    [Fact]
    public async Task DoubleEncodedToolResult_StillProducesRequestLocalMentions()
    {
        var tool = new RecordingTool("search_task_masters",
            JsonSerializer.Serialize("""[{"id":"tm-1","name":"Alice"}]"""));
        var client = new RecordingClient((round, _) => Task.FromResult(round == 1
            ? Calls(Call("call", tool.Name)) : Answer("Found Alice.")));
        var (service, _) = Service(client, [tool]);
        var response = await service.ChatAsync(new ChatRequest { Message = "find someone" }, Context, CancellationToken.None);
        var mention = Assert.Single(response.Mentions);
        Assert.Equal("tm-1", mention.Id);
        Assert.Equal("Alice", mention.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateIds_AreRejectedBeforeAnyToolInTheBatchExecutes(bool collidesWithSeed)
    {
        var tool = new RecordingTool("search_task_masters", "[]");
        var client = new RecordingClient((_, _) => Task.FromResult(Calls(
            Call("live-call", tool.Name),
            Call(collidesWithSeed ? "seed_example_0_call_0" : "live-call", tool.Name))));
        var (service, _) = Service(client, [tool]);
        var response = await service.ChatAsync(new ChatRequest { Message = "search" }, Context, CancellationToken.None);
        Assert.Contains("invalid response", response.Answer);
        Assert.Equal(0, tool.CallCount);
        Assert.Empty(response.Sources);
        Assert.Empty(response.Mentions);
    }

    [Fact]
    public async Task MultipleTools_AreAwaitedSequentially()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new RecordingTool("first", "[]", async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return "first result";
        });
        var second = new RecordingTool("second", "second result");
        var client = new RecordingClient((round, _) => Task.FromResult(round == 1
            ? Calls(Call("first-id", first.Name), Call("second-id", second.Name)) : Answer("done")));
        var (service, _) = Service(client, [first, second]);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var chat = service.ChatAsync(new ChatRequest { Message = "do both" }, Context, guard.Token);
        try
        {
            await started.Task.WaitAsync(guard.Token);
            Assert.Equal(0, second.CallCount);
        }
        finally
        {
            release.TrySetResult();
        }
        await chat;
        Assert.Equal(1, second.CallCount);
    }

    [Theory]
    [InlineData(ChatModelFailureCategory.LocalModelUnavailable, 404,
        "The AI model is still being set up and isn't available yet. Please try again in a few minutes.")]
    [InlineData(ChatModelFailureCategory.Timeout, null,
        "The AI model took too long to respond. Please try again.")]
    [InlineData(ChatModelFailureCategory.ProviderUnavailable, null,
        "I could not reach the AI model right now. Please try again shortly.")]
    [InlineData(ChatModelFailureCategory.ProviderUnavailable, 500,
        "The AI model ran into a problem answering your request. Please try again shortly.")]
    [InlineData(ChatModelFailureCategory.ProtocolError, 200,
        "The AI assistant returned an invalid response. Please try again.")]
    public async Task ModelFailureAfterToolExecution_KeepsSafeTextAndClearsMetadata(
        ChatModelFailureCategory category, int? status, string answer)
    {
        var tool = new RecordingTool("search_task_masters", """[{"id":"live","name":"Alice"}]""");
        var client = new RecordingClient((round, _) => round == 1
            ? Task.FromResult(Calls(Call("call", tool.Name)))
            : throw new ChatModelException(category, new ChatModelFailureMetadata(httpStatusCode: status)));
        var (service, resolver) = Service(client, [tool]);
        var response = await service.ChatAsync(new ChatRequest { Message = "search" }, Context, CancellationToken.None);

        Assert.Equal(answer, response.Answer);
        Assert.Equal("configured-model", response.Model);
        Assert.Empty(response.Sources);
        Assert.Empty(response.Mentions);
        Assert.Equal(1, tool.CallCount);
        resolver.Verify(r => r.Resolve("chat"), Times.Once);
    }

    [Theory]
    [InlineData(
        ChatModelFailureCategory.Timeout,
        "The AI assistant is temporarily busy. Please try again shortly.")]
    [InlineData(
        ChatModelFailureCategory.RateLimited,
        "The AI assistant is temporarily busy. Please try again shortly.")]
    [InlineData(
        ChatModelFailureCategory.ProviderUnavailable,
        "The AI assistant is temporarily busy. Please try again shortly.")]
    [InlineData(
        ChatModelFailureCategory.Authentication,
        "The AI assistant is unavailable right now.")]
    [InlineData(
        ChatModelFailureCategory.CreditsExhausted,
        "The AI assistant is unavailable right now.")]
    [InlineData(
        ChatModelFailureCategory.InvalidRequest,
        "The AI assistant is unavailable right now.")]
    [InlineData(
        ChatModelFailureCategory.PolicyRejected,
        "The AI assistant could not process that request.")]
    [InlineData(
        ChatModelFailureCategory.ProtocolError,
        "The AI assistant returned an invalid response. Please try again.")]
    public async Task OpenRouterFailureAfterToolExecution_UsesSafeProviderTextAndClearsMetadata(
        ChatModelFailureCategory category,
        string answer)
    {
        var tool = new RecordingTool(
            "search_task_masters",
            """[{"id":"live","name":"Alice"}]""");
        var client = new RecordingClient(
            (round, _) => round == 1
                ? Task.FromResult(Calls(Call("call", tool.Name)))
                : throw new ChatModelException(category),
            provider: ChatModelProviders.OpenRouter);
        var (service, _) = Service(client, [tool]);

        var response = await service.ChatAsync(
            new ChatRequest { Message = "search" },
            Context,
            CancellationToken.None);

        Assert.Equal(answer, response.Answer);
        Assert.Equal("configured-model", response.Model);
        Assert.Empty(response.Sources);
        Assert.Empty(response.Mentions);
        Assert.Equal(1, tool.CallCount);
    }

    [Fact]
    public async Task ToolFailure_KeepsResultIdAndLetsModelFinish()
    {
        var tool = new RecordingTool("search_task_masters", "", _ => throw new HttpRequestException("unavailable"));
        var client = new RecordingClient((round, _) => Task.FromResult(round == 1
            ? Calls(Call("failed-call", tool.Name)) : Answer("The data is unavailable.")));
        var (service, _) = Service(client, [tool]);
        var response = await service.ChatAsync(new ChatRequest { Message = "search" }, Context, CancellationToken.None);
        var result = client.Requests[1].Messages[^1];
        Assert.Equal("failed-call", result.ToolCallId);
        Assert.Contains("tool_unavailable", result.Content);
        Assert.Equal("The data is unavailable.", response.Answer);
        Assert.Empty(response.Mentions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellation_PropagatesFromModelAndTool(bool duringTool)
    {
        using var caller = new CancellationTokenSource();
        Task<string> Cancel(CancellationToken token)
        {
            caller.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult("unreachable");
        }
        var tool = new RecordingTool("search_task_masters", "", Cancel);
        var client = new RecordingClient(async (_, token) =>
        {
            if (duringTool) return Calls(Call("call", tool.Name));
            await Cancel(token);
            return Answer("unreachable");
        });
        var (service, _) = Service(client, [tool]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ChatAsync(new ChatRequest { Message = "hello" }, Context, caller.Token));
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task TypedCallerCancellation_EscapesFailureMapping()
    {
        var client = new RecordingClient((_, _) =>
            throw new ChatModelException(ChatModelFailureCategory.CallerCancellation));
        var (service, _) = Service(client, []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ChatAsync(
                new ChatRequest { Message = "hello" },
                Context,
                CancellationToken.None));
    }

    [Fact]
    public async Task RequestDeadline_CoversToolExecutionAndDoesNotStartAnotherRound()
    {
        var tool = new RecordingTool("search_task_masters", "", async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        });
        var client = new RecordingClient((_, _) => Task.FromResult(Calls(Call("call", tool.Name))),
            requestTimeoutSeconds: 1);
        var (service, _) = Service(client, [tool]);
        var response = await service.ChatAsync(new ChatRequest { Message = "search" }, Context, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("The AI model took too long to respond. Please try again.", response.Answer);
        Assert.Single(client.Requests);
        Assert.Empty(response.Sources);
        Assert.Empty(response.Mentions);
    }

    [Fact]
    public async Task InformationAndDebugLogs_ExcludePromptsArgumentsResultsAndCompletions()
    {
        var logger = new RecordingLogger<AiAssistantService>();
        var tool = new RecordingTool(
            "search_task_masters",
            """[{"id":"sentinel-result-id","name":"sentinel-result-name"}]""");
        var client = new RecordingClient((round, _) => Task.FromResult(round == 1
            ? Calls(Call(
                "call",
                tool.Name,
                """{"sentinel_argument_key":"sentinel-argument-value","minRating":9}"""))
            : Answer("sentinel-completion")));
        var (service, _) = Service(client, [tool], logger);

        await service.ChatAsync(new ChatRequest
        {
            Message = "sentinel-current-prompt",
            History = [new() { Role = "user", Content = "sentinel-history-prompt" }]
        }, Context, CancellationToken.None);

        Assert.DoesNotContain(
            logger.Entries.Where(entry => entry.Level is LogLevel.Debug or LogLevel.Information),
            entry => entry.Message.Contains("sentinel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ToolExceptionDetails_AreNotLogged()
    {
        var logger = new RecordingLogger<AiAssistantService>();
        var tool = new RecordingTool(
            "search_task_masters",
            "",
            _ => throw new InvalidOperationException("sentinel-private-exception"));
        var client = new RecordingClient((round, _) => Task.FromResult(round == 1
            ? Calls(Call("call", tool.Name))
            : Answer("safe answer")));
        var (service, _) = Service(client, [tool], logger);

        await service.ChatAsync(
            new ChatRequest { Message = "search" },
            Context,
            CancellationToken.None);

        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Message.Contains("sentinel", StringComparison.OrdinalIgnoreCase)
                || entry.Exception?.ToString().Contains(
                    "sentinel",
                    StringComparison.OrdinalIgnoreCase) == true);
    }

    private static readonly ToolExecutionContext Context = new("alice", ["calendar.read"], "correlation-1");

    private static (AiAssistantService, Mock<ITaskChatModelResolver>) Service(
        RecordingClient client,
        IReadOnlyList<IToolDefinition> tools,
        ILogger<AiAssistantService>? logger = null)
    {
        var resolver = new Mock<ITaskChatModelResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.Resolve("chat")).Returns(client);
        var prompt = new AssistantPrompt(Options.Create(new PromptOptions
        {
            SystemPrompt = "test system prompt",
            FewShotExamples = [new FewShotExample
            {
                Turns = [
                    new() { Role = "user", Content = "a landscaper with a rating of at least 4.5" },
                    new() { Role = "assistant", ToolCalls = [new()
                    {
                        Id = "seed_example_0_call_0", Name = "search_task_masters",
                        Arguments = new() { ["category"] = "landscaping", ["minRating"] = "4.5" }
                    }] },
                    new() { Role = "tool", ToolCallId = "seed_example_0_call_0",
                        Content = """[{"id":"seed-1","name":"Seeded Example"}]""" },
                    new() { Role = "assistant", Content = "Seeded final answer." }
                ]
            }]
        }));
        return (new AiAssistantService(prompt, resolver.Object,
            new ToolRegistry(tools), logger ?? NullLogger<AiAssistantService>.Instance), resolver);
    }

    private static ChatModelToolCall Call(string id, string name, string arguments = "{}")
    {
        using var document = JsonDocument.Parse(arguments);
        return new ChatModelToolCall(id, name, document.RootElement);
    }

    private static ChatModelResponse Calls(params ChatModelToolCall[] calls) =>
        new(new ChatModelMessage("assistant", null, calls));
    private static ChatModelResponse Answer(string content, string? resolvedModel = null) =>
        new(new ChatModelMessage("assistant", content), resolvedModel: resolvedModel);

    private sealed class RecordingClient(
        Func<int, CancellationToken, Task<ChatModelResponse>> respond,
        int requestTimeoutSeconds = 300,
        string provider = ChatModelProviders.Ollama) : ITaskChatModelClient
    {
        public TaskModelProfile Profile { get; } =
            new("chat", provider, "configured-model", 1, requestTimeoutSeconds, 128);
        public List<(IReadOnlyList<ChatModelMessage> Messages, IReadOnlyList<ChatModelTool>? Tools,
            CancellationToken Token)> Requests { get; } = [];

        public Task<ChatModelResponse> ChatAsync(
            IReadOnlyList<ChatModelMessage> messages, IReadOnlyList<ChatModelTool>? tools,
            CancellationToken cancellationToken)
        {
            Requests.Add((messages.ToArray(), tools, cancellationToken));
            return respond(Requests.Count, cancellationToken);
        }
    }

    private sealed class RecordingTool(
        string name, string result, Func<CancellationToken, Task<string>>? execute = null) : IToolDefinition
    {
        public string Name => name;
        public string Result => result;
        public string Description => "test tool";
        public object ParametersSchema => new { type = "object" };
        public int CallCount { get; private set; }
        public ToolExecutionContext? LastContext { get; private set; }
        public IReadOnlyDictionary<string, string>? LastArgs { get; private set; }

        public Task<string> ExecuteAsync(ToolExecutionContext context,
            IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
        {
            LastContext = context;
            LastArgs = arguments;
            CallCount++;
            return execute?.Invoke(cancellationToken) ?? Task.FromResult(result);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
