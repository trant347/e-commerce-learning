using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using ai_assistant_service.Configuration;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services;
using ai_assistant_service.Services.ChatModels;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Telemetry;
using ai_assistant_service.Services.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using T = ai_assistant_service.Services.Telemetry.AiAssistantTelemetry;

namespace ai_assistant_service.Tests;

// Listeners observe process-wide sources, so this collection runs alone.
[CollectionDefinition(nameof(AiAssistantTelemetryCollection), DisableParallelization = true)]
public sealed class AiAssistantTelemetryCollection;

[Collection(nameof(AiAssistantTelemetryCollection))]
public sealed class AiAssistantTelemetryTests
{
    private const string Sentinel = "sentinel";

    [Fact]
    public async Task OpenRouterChat_RecordsMetadataForChatRoundsToolsRetriesAndTokens()
    {
        using var capture = new TelemetryCapture();
        using var handler = new OpenRouterHandler(
            ToolCallReply("search_task_masters"),
            UnavailableReply(),
            FinalReply());
        var tool = new SentinelTool("search_task_masters");

        var response = await ChatAsync(handler, tool);

        Assert.Equal("sentinel completion", response.Answer);
        Assert.Equal(1, tool.Calls);

        var chat = Assert.Single(capture.Activities, a => a.OperationName == T.ChatActivityName);
        Assert.Equal("chat", chat.GetTagItem(T.TaskTag));
        Assert.Equal(ChatModelProviders.OpenRouter, chat.GetTagItem(T.ProviderTag));
        Assert.Equal("vendor/tool-model", chat.GetTagItem(T.RequestedModelTag));
        Assert.Equal(T.Success, chat.GetTagItem(T.OutcomeTag));
        Assert.Equal(2, chat.GetTagItem(T.ChatRoundsTag));
        Assert.Equal(1, chat.GetTagItem(T.ChatToolCallsTag));
        Assert.Equal(false, chat.GetTagItem(T.ChatRoundLimitTag));

        var rounds = capture.Activities
            .Where(a => a.OperationName == T.ModelRoundActivityName)
            .OrderBy(a => a.GetTagItem(T.RoundNumberTag))
            .ToList();
        Assert.Equal(2, rounds.Count);
        Assert.All(rounds, round => Assert.Equal(chat.SpanId, round.ParentSpanId));
        Assert.Equal([1, 2], rounds.Select(r => (int)r.GetTagItem(T.RoundNumberTag)!));
        Assert.Equal(1, rounds[0].GetTagItem(T.RoundToolCallsTag));
        Assert.Equal(0, rounds[0].GetTagItem(T.RetryCountTag));
        Assert.Equal(1, rounds[1].GetTagItem(T.RetryCountTag));
        Assert.Equal("vendor/resolved-model", rounds[1].GetTagItem(T.ResolvedModelTag));
        Assert.Equal("gen-final", rounds[1].GetTagItem(T.ProviderRequestIdTag));
        Assert.Equal(11L, rounds[1].GetTagItem(T.InputTokensTag));
        Assert.Equal(7L, rounds[1].GetTagItem(T.OutputTokensTag));
        Assert.Equal(18L, rounds[1].GetTagItem(T.TotalTokensTag));
        var retry = Assert.Single(rounds[1].Events);
        Assert.Equal(T.RetryEventName, retry.Name);
        Assert.Contains(retry.Tags, tag => tag.Key == T.ErrorTypeTag
            && Equals(tag.Value, nameof(ChatModelFailureCategory.ProviderUnavailable)));
        Assert.Contains(retry.Tags, tag => tag.Key == T.HttpStatusTag && Equals(tag.Value, 503));

        var retryMetric = Assert.Single(capture.Measurements(T.RetriesMetric));
        Assert.Equal(1, retryMetric.Value);
        Assert.Equal("chat", retryMetric.Tag(T.TaskTag));
        Assert.Equal(nameof(ChatModelFailureCategory.ProviderUnavailable), retryMetric.Tag(T.ErrorTypeTag));

        var toolMetric = Assert.Single(capture.Measurements(T.ToolCallsMetric));
        Assert.Equal("search_task_masters", toolMetric.Tag(T.ToolNameTag));
        Assert.Equal(T.Success, toolMetric.Tag(T.OutcomeTag));

        var tokens = capture.Measurements(T.TokensMetric)
            .ToDictionary(m => (string)m.Tag(T.TokenTypeTag)!, m => m.Value);
        Assert.Equal(new Dictionary<string, double> { ["input"] = 11, ["output"] = 7, ["total"] = 18 },
            tokens);

        var chatDuration = Assert.Single(capture.Measurements(T.ChatDurationMetric));
        Assert.Equal(T.Success, chatDuration.Tag(T.OutcomeTag));
        Assert.Equal(2, capture.Measurements(T.ModelRoundDurationMetric).Count);
        Assert.Empty(capture.Measurements(T.FailuresMetric));

        // Request IDs and resolved models belong on traces, never metric dimensions.
        Assert.All(capture.AllMeasurements, measurement =>
        {
            Assert.DoesNotContain(measurement.Tags, tag => tag.Key is T.ProviderRequestIdTag
                or T.ResolvedModelTag);
            Assert.DoesNotContain(measurement.Tags, tag => tag.Value?.ToString()?.Contains("gen-") == true);
        });
        capture.AssertNoSentinelValues();
    }

    [Fact]
    public async Task ProviderFailure_RecordsBoundedFailureMetadata()
    {
        using var capture = new TelemetryCapture();
        using var handler = new OpenRouterHandler(Reply(
            """{"error":{"code":401,"message":"sentinel provider message"}}""",
            HttpStatusCode.Unauthorized));

        var response = await ChatAsync(handler, new SentinelTool("search_task_masters"));

        Assert.Equal("The AI assistant is unavailable right now.", response.Answer);
        var chat = Assert.Single(capture.Activities, a => a.OperationName == T.ChatActivityName);
        Assert.Equal(ActivityStatusCode.Error, chat.Status);
        Assert.Equal(T.Failure, chat.GetTagItem(T.OutcomeTag));
        Assert.Equal(nameof(ChatModelFailureCategory.Authentication), chat.GetTagItem(T.ErrorTypeTag));
        Assert.Equal(401, chat.GetTagItem(T.HttpStatusTag));

        var round = Assert.Single(capture.Activities, a => a.OperationName == T.ModelRoundActivityName);
        Assert.Equal(ActivityStatusCode.Error, round.Status);

        var failure = Assert.Single(capture.Measurements(T.FailuresMetric));
        Assert.Equal("chat", failure.Tag(T.TaskTag));
        Assert.Equal(nameof(ChatModelFailureCategory.Authentication), failure.Tag(T.ErrorTypeTag));
        var roundDuration = Assert.Single(capture.Measurements(T.ModelRoundDurationMetric));
        Assert.Equal(T.Failure, roundDuration.Tag(T.OutcomeTag));
        Assert.Equal(401, roundDuration.Tag(T.HttpStatusTag));
        Assert.Empty(capture.Measurements(T.RetriesMetric));
        capture.AssertNoSentinelValues();
    }

    [Fact]
    public async Task ModelInventedToolName_FailsBeforeExecutionAndIsNeverALabel()
    {
        using var capture = new TelemetryCapture();
        using var handler = new OpenRouterHandler(
            ToolCallReply("sentinel_invented_tool"),
            FinalReply());
        var tool = new SentinelTool("search_task_masters");

        await ChatAsync(handler, tool);

        Assert.Equal(0, tool.Calls);
        Assert.Empty(capture.Measurements(T.ToolCallsMetric));
        var failure = Assert.Single(capture.Measurements(T.FailuresMetric));
        Assert.Equal(nameof(ChatModelFailureCategory.ProtocolError), failure.Tag(T.ErrorTypeTag));
        capture.AssertNoSentinelValues();
    }

    [Fact]
    public async Task SyntheticTasks_RecordTheirOwnValidatedProfiles()
    {
        using var capture = new TelemetryCapture();
        var ollama = new StubAdapter();
        var openRouter = new StubAdapter();
        var resolver = new TaskChatModelResolver(
            new TaskModelProfileCatalog(Options.Create(new AiModelsOptions
            {
                Tasks = new Dictionary<string, TaskModelOptions>
                {
                    ["chat"] = Profile(ChatModelProviders.Ollama, "local-model"),
                    ["synthetic"] = Profile(ChatModelProviders.OpenRouter, "vendor/other")
                }
            })),
            [
                new ChatModelClientRegistration(ChatModelProviders.Ollama, ollama),
                new ChatModelClientRegistration(ChatModelProviders.OpenRouter, openRouter)
            ]);
        var messages = new[] { new ChatModelMessage(ChatModelRoles.User, "sentinel prompt") };

        await Task.WhenAll(
            resolver.Resolve("chat").ChatAsync(messages, null, CancellationToken.None),
            resolver.Resolve("synthetic").ChatAsync(messages, null, CancellationToken.None));
        Assert.Throws<ChatModelException>(() => resolver.Resolve("sentinel_unknown_task"));

        var rounds = capture.Measurements(T.ModelRoundDurationMetric);
        Assert.Equal(2, rounds.Count);
        Assert.Contains(rounds, m => Equals(m.Tag(T.TaskTag), "chat")
            && Equals(m.Tag(T.ProviderTag), ChatModelProviders.Ollama)
            && Equals(m.Tag(T.RequestedModelTag), "local-model"));
        Assert.Contains(rounds, m => Equals(m.Tag(T.TaskTag), "synthetic")
            && Equals(m.Tag(T.ProviderTag), ChatModelProviders.OpenRouter)
            && Equals(m.Tag(T.RequestedModelTag), "vendor/other"));
        capture.AssertNoSentinelValues();
    }

    [Fact]
    public async Task CallerCancellation_IsRecordedAsCanceledNotFailure()
    {
        using var capture = new TelemetryCapture();
        var profile = new TaskModelProfile("chat", ChatModelProviders.Ollama, "local-model", 10, 30, 64);
        var client = new TaskChatModelClient(profile, new StubAdapter(waitForCancellation: true));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ChatAsync(
            [new ChatModelMessage(ChatModelRoles.User, "hello")], null, caller.Token));

        var round = Assert.Single(capture.Measurements(T.ModelRoundDurationMetric));
        Assert.Equal(T.Canceled, round.Tag(T.OutcomeTag));
        Assert.Null(round.Tag(T.ErrorTypeTag));
        Assert.Empty(capture.Measurements(T.FailuresMetric));
    }

    [Fact]
    public void CategoryDecisions_RecordBoundedOutcomesAndTraceOnlyProviderMetadata()
    {
        using var capture = new TelemetryCapture();
        const string version =
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using (var decision = T.StartCategoryDecision(
                   "Jev", "typesafe/jev-1.13", version, CancellationToken.None))
        {
            decision.ReportProviderUsage("typesafe/resolved-model", 14, 6);
            decision.RepairAttempted();
            decision.Succeeded(CategoryDecisionStatus.Resolved);
        }
        using (var failed = T.StartCategoryDecision(
                   "Jev", "typesafe/jev-1.13", version, CancellationToken.None))
        {
            failed.Failed(nameof(JevFailureCategory.InsufficientCredits), 402);
        }

        var decisions = capture.Measurements(T.CategoryDecisionsMetric);
        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, m => Equals(m.Tag(T.OutcomeTag), nameof(CategoryDecisionStatus.Resolved)));
        Assert.Contains(decisions, m => Equals(m.Tag(T.ErrorTypeTag), nameof(JevFailureCategory.InsufficientCredits)));
        Assert.All(decisions, m =>
        {
            Assert.Equal("category", m.Tag(T.TaskTag));
            Assert.Equal("Jev", m.Tag(T.ProviderTag));
            Assert.Equal("typesafe/jev-1.13", m.Tag(T.RequestedModelTag));
            Assert.Null(m.Tag(T.ResolvedModelTag));
            Assert.Null(m.Tag("ai.category.catalog_version"));
        });
        Assert.Single(capture.Measurements(T.CategoryRepairsMetric));
        Assert.Equal(2, capture.Measurements(T.CategoryDecisionDurationMetric).Count);

        var activities = capture.Activities
            .Where(activity => activity.OperationName == T.CategoryDecisionActivityName).ToList();
        Assert.Equal(2, activities.Count);
        Assert.Contains(activities, activity =>
            Equals(activity.GetTagItem(T.ResolvedModelTag), "typesafe/resolved-model")
            && Equals(activity.GetTagItem("ai.category.catalog_version"), version)
            && Equals(activity.GetTagItem(T.InputTokensTag), 14));
        capture.AssertNoSentinelValues();
    }

    private static async Task<ChatResponse> ChatAsync(OpenRouterHandler handler, IToolDefinition tool)
    {
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
                    ["OpenRouter:ApiKey"] = "sentinel-api-key",
                    ["OpenRouter:MaxRetries"] = "1"
                }))
            .ConfigureServices((context, services) =>
            {
                services.AddAiModelRouting(context.Configuration);
                services.AddHttpClient<OpenRouterChatModelClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddSingleton(tool);
                services.AddSingleton<ToolRegistry>();
                services.AddScoped<IAiAssistantService, AiAssistantService>();
            })
            .Build();
        await host.StartAsync();
        using var scope = host.Services.CreateScope();
        var response = await scope.ServiceProvider.GetRequiredService<IAiAssistantService>().ChatAsync(
            new ChatRequest { Message = "sentinel prompt" },
            new ToolExecutionContext("sentinel-user", ["calendar.read"], "sentinel-correlation"),
            CancellationToken.None);
        await host.StopAsync();
        return response;
    }

    private static TaskModelOptions Profile(string provider, string model) => new()
    {
        Provider = provider,
        Model = model,
        TimeoutSeconds = 10,
        RequestTimeoutSeconds = 30,
        MaxOutputTokens = 64
    };

    private static HttpResponseMessage ToolCallReply(string toolName) => Reply($$$"""
        {"id":"gen-tool","model":"vendor/resolved-model","choices":[{"message":{"role":"assistant","tool_calls":[{
          "id":"live-call","type":"function","function":{
            "name":"{{{toolName}}}","arguments":"{\"query\":\"sentinel argument\"}"
          }
        }]}}]}
        """);

    private static HttpResponseMessage UnavailableReply()
    {
        var reply = Reply(
            """{"error":{"code":503,"message":"sentinel unavailable"}}""",
            HttpStatusCode.ServiceUnavailable);
        reply.Headers.TryAddWithoutValidation("Retry-After", "0");
        return reply;
    }

    private static HttpResponseMessage FinalReply() => Reply("""
        {"id":"gen-final","model":"vendor/resolved-model",
         "usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18},
         "choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"sentinel completion"}}]}
        """);

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class OpenRouterHandler(params HttpResponseMessage[] replies) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _replies = new(replies);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_replies.Dequeue());
    }

    private sealed class SentinelTool(string name) : IToolDefinition
    {
        public string Name => name;
        public string Description => "Search TaskMasters.";
        public object ParametersSchema => new { type = "object" };
        public int Calls { get; private set; }

        public Task<string> ExecuteAsync(ToolExecutionContext executionContext,
            IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult("""[{"id":"sentinel-id","name":"Sentinel Result"}]""");
        }
    }

    private sealed class StubAdapter(bool waitForCancellation = false) : IChatModelClient
    {
        public async Task<ChatModelResponse> ChatAsync(
            ChatModelRequest request, CancellationToken cancellationToken)
        {
            if (waitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new ChatModelResponse(new ChatModelMessage(ChatModelRoles.Assistant, "sentinel completion"));
        }
    }

    private sealed record Measurement(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public object? Tag(string key) => Tags.FirstOrDefault(tag => tag.Key == key).Value;
    }

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener = new();
        private readonly ConcurrentQueue<Activity> _activities = new();
        private readonly ConcurrentQueue<Measurement> _measurements = new();

        public TelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == T.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _activities.Enqueue
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == T.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) => Record(instrument, value, tags));
            _meterListener.SetMeasurementEventCallback<double>(
                (instrument, value, tags, _) => Record(instrument, value, tags));
            _meterListener.Start();
        }

        public IReadOnlyCollection<Activity> Activities => _activities.ToArray();
        public IReadOnlyCollection<Measurement> AllMeasurements => _measurements.ToArray();

        public List<Measurement> Measurements(string instrument) =>
            _measurements.Where(m => m.Instrument == instrument).ToList();

        public void AssertNoSentinelValues()
        {
            var values = _activities.SelectMany(activity => activity.TagObjects
                    .Select(tag => tag.Value)
                    .Concat(activity.Events.SelectMany(e => e.Tags.Select(tag => tag.Value)))
                    .Append(activity.StatusDescription)
                    .Append(activity.DisplayName))
                .Concat(_measurements.SelectMany(m => m.Tags.Select(tag => tag.Value)));
            Assert.DoesNotContain(values, value =>
                value?.ToString()?.Contains(Sentinel, StringComparison.OrdinalIgnoreCase) == true);
        }

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
            _measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray()));
    }
}
