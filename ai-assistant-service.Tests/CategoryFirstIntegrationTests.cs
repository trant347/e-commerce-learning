using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services;
using ai_assistant_service.Services.ChatModels;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Mcp;
using ai_assistant_service.Services.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ai_assistant_service.Tests;

public class CategoryFirstIntegrationTests
{
    private const string Version =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Filters = """
        {"hard":{"maxRate":{"value":50,"unit":"usd_per_hour","evidence":"under $50/hour"},
                  "location":{"value":"Chicago, IL","unit":"place","evidence":"Chicago, IL"}},
         "soft":[{"field":"minRating","value":4.5,"unit":"rating_0_5","evidence":"preferably rated 4.5 stars"}],
         "unsupportedHard":[]}
        """;
    private const string NoFilters = """{"hard":{},"soft":[],"unsupportedHard":[]}""";
    private static readonly ToolExecutionContext Context = new("alice", ["calendar.read"], "jev-test");

    [Fact]
    public async Task ResolvedJev_PrefetchesLockedCategoryAndHardFilters_ButNotSoftPreferences()
    {
        var model = new RecordingChat(round => round switch
        {
            1 => Answer(Filters),
            2 => Answer("Found Alice."),
            _ => throw new InvalidOperationException()
        });
        var jev = new RecordingJev(_ => Resolved("carpentry"));
        var tool = new RecordingTool("search_task_masters", """[{"id":"tm-1","name":"Alice"}]""");
        var assistant = Service(model, jev, tool);
        var request = new ChatRequest
        {
            Message = "Find a carpenter in Chicago, IL under $50/hour, preferably rated 4.5 stars",
            History = [new ChatHistoryMessage { Role = "user", Content = "Please repair my table" }]
        };

        var answer = await assistant.ChatAsync(request, Context, CancellationToken.None);

        Assert.Equal("Found Alice.", answer.Answer);
        Assert.Equal(["search_task_masters"], answer.Sources);
        Assert.Equal("Alice", Assert.Single(answer.Mentions).Name);
        Assert.Equal(1, jev.Calls);
        Assert.Equal(["Please repair my table"], jev.LastRequest!.ClarificationHistory);
        Assert.Equal(1, tool.Calls);
        Assert.Equal("carpentry", tool.Arguments!["category"]);
        Assert.Equal("50", tool.Arguments["maxRate"]);
        Assert.Equal("Chicago, IL", tool.Arguments["location"]);
        Assert.False(tool.Arguments.ContainsKey("minRating"));
        Assert.Equal(2, model.Requests.Count);
        Assert.Null(model.Requests[0].Tools);
        Assert.Contains(model.Requests[1].Messages, message =>
            message.Role == "tool" && message.Content!.Contains("Alice", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("decision:needs_clarification", CategoryDecisionStatus.NeedsClarification)]
    [InlineData("decision:no_match", CategoryDecisionStatus.NoMatch)]
    public async Task Abstention_NeverSearchesOrExtractsFilters(
        string selected, CategoryDecisionStatus _)
    {
        var model = new RecordingChat(_ => throw new InvalidOperationException("Model must not run"));
        var jev = new RecordingJev(_ => Resolved(selected));
        var search = new RecordingTool("search_task_masters", "[]");

        var response = await Service(model, jev, search).ChatAsync(
            new ChatRequest { Message = "Please help with a task" }, Context, CancellationToken.None);

        Assert.Empty(response.Sources);
        Assert.Equal(0, search.Calls);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task UncertainIntent_NeverSearches()
    {
        var jev = new RecordingJev(_ => Resolved("carpentry") with
        {
            Intent = new JevChoiceAnswer(
                "service_search", 0.2,
                new Dictionary<string, double>
                {
                    ["service_search"] = 0.55, ["not_applicable"] = 0.45
                })
        });
        var search = new RecordingTool("search_task_masters", "[]");
        var model = new RecordingChat(_ => throw new InvalidOperationException());

        await Service(model, jev, search).ChatAsync(
            new ChatRequest { Message = "help" }, Context, CancellationToken.None);

        Assert.Equal(0, search.Calls);
    }

    [Fact]
    public async Task NotApplicable_KeepsBookingToolFlowWithoutProviderSearch()
    {
        var model = new RecordingChat(round => round == 1
            ? Calls("book", "get_bookings")
            : Answer("Your booking is confirmed."));
        var jev = new RecordingJev(_ => Resolved("carpentry") with
        {
            Intent = new JevChoiceAnswer(
                "not_applicable", 0.9,
                new Dictionary<string, double>
                {
                    ["service_search"] = 0.1, ["not_applicable"] = 0.9
                })
        });
        var booking = new RecordingTool("get_bookings", """{"id":"booking-1"}""");
        var search = new RecordingTool("search_task_masters", "[]");

        var response = await Service(model, jev, search, booking).ChatAsync(
            new ChatRequest { Message = "Show my bookings" }, Context, CancellationToken.None);

        Assert.Equal("Your booking is confirmed.", response.Answer);
        Assert.Equal(["get_bookings"], response.Sources);
        Assert.Equal(1, booking.Calls);
        Assert.Equal(0, search.Calls);
    }

    [Fact]
    public async Task LateNativeSearch_IsReclassifiedAndUsesLockedCategory()
    {
        var model = new RecordingChat(round => round switch
        {
            1 => Calls("late", "search_task_masters", """{"category":"gardening","maxRate":10}"""),
            2 => Answer(NoFilters),
            3 => Answer("No providers were returned."),
            _ => throw new InvalidOperationException()
        });
        var jev = new RecordingJev(call => call == 1
            ? Resolved("carpentry") with
            {
                Intent = new JevChoiceAnswer(
                    "not_applicable", 0.9,
                    new Dictionary<string, double> { ["service_search"] = 0.1, ["not_applicable"] = 0.9 })
            }
            : Resolved("carpentry"));
        var search = new RecordingTool("search_task_masters", "[]");

        var answer = await Service(model, jev, search).ChatAsync(
            new ChatRequest { Message = "Find a carpenter" }, Context, CancellationToken.None);

        Assert.Equal(2, jev.Calls);
        Assert.Equal(1, search.Calls);
        Assert.Equal("carpentry", search.Arguments!["category"]);
        Assert.False(search.Arguments.ContainsKey("maxRate"));
        Assert.Equal(["search_task_masters"], answer.Sources);
    }

    [Fact]
    public async Task LateSearch_WithNoValidDecision_IsBlocked()
    {
        var model = new RecordingChat(_ =>
            Calls("inline", "search_task_masters", """{"category":"gardening"}"""));
        var jev = new RecordingJev(_ => Resolved("carpentry") with
        {
            Intent = new JevChoiceAnswer(
                "not_applicable", 0.9,
                new Dictionary<string, double> { ["service_search"] = 0.1, ["not_applicable"] = 0.9 })
        });
        var search = new RecordingTool("search_task_masters", "[]");

        var response = await Service(model, jev, search).ChatAsync(
            new ChatRequest { Message = "Hello" }, Context, CancellationToken.None);

        Assert.Equal(0, search.Calls);
        Assert.Empty(response.Sources);
        Assert.Equal(2, jev.Calls);
    }

    [Fact]
    public async Task OllamaInlineRecoveredSearch_CannotBypassDecisionGate()
    {
        using var http = new HttpClient(new InlineSearchHandler())
        {
            BaseAddress = new Uri("http://ollama-test:11434")
        };
        var adapter = new OllamaChatModelClient(http);
        var chat = new TaskChatModelClient(
            new TaskModelProfile("chat", ChatModelProviders.Ollama, "qwen3:8b", 10, 60, 1024),
            adapter);
        var jev = new RecordingJev(_ => Resolved("carpentry") with
        {
            Intent = new JevChoiceAnswer(
                "not_applicable", 0.9,
                new Dictionary<string, double> { ["service_search"] = 0.1, ["not_applicable"] = 0.9 })
        });
        var search = new RecordingTool("search_task_masters", "[]");

        var response = await Service(
            new RecordingChat(_ => throw new InvalidOperationException()), jev, search,
            chatOverride: chat).ChatAsync(
                new ChatRequest { Message = "Hello" }, Context, CancellationToken.None);

        Assert.Empty(response.Sources);
        Assert.Equal(0, search.Calls);
        Assert.Equal(2, jev.Calls);
    }

    [Fact]
    public async Task ResolvedDecision_RejectsLaterCategoryOrFilterOverride()
    {
        var model = new RecordingChat(round => round switch
        {
            1 => Answer(NoFilters),
            2 => Calls("override", "search_task_masters", """{"category":"gardening","minRate":10}"""),
            3 => Answer("No providers."),
            _ => throw new InvalidOperationException()
        });
        var search = new RecordingTool("search_task_masters", "[]");

        var response = await Service(model, new RecordingJev(_ => Resolved("carpentry")), search)
            .ChatAsync(new ChatRequest { Message = "Find a carpenter" }, Context, CancellationToken.None);

        Assert.Equal(1, search.Calls);
        Assert.Equal("carpentry", search.Arguments!["category"]);
        Assert.False(search.Arguments.ContainsKey("minRate"));
        Assert.Equal(["search_task_masters"], response.Sources);
    }

    [Fact]
    public async Task ChatModelSemanticProvider_RejectsUnknownCategory()
    {
        var model = new RecordingChat(_ =>
            Answer("""{"intent":"service_search","categoryId":"fabricated"}"""));
        var search = new RecordingTool("search_task_masters", "[]");
        var response = await Service(model, new RecordingJev(_ => throw new InvalidOperationException()),
            search, semanticProvider: CategorySemanticProviders.ChatModel).ChatAsync(
                new ChatRequest { Message = "Find help" }, Context, CancellationToken.None);
        Assert.Equal(0, search.Calls);
        Assert.Empty(response.Sources);
    }

    [Fact]
    public async Task ChatModelSemanticProvider_UsesStructuredCategoryAndFilters()
    {
        var model = new RecordingChat(round => round switch
        {
            1 => Answer("""{"intent":"service_search","categoryId":"carpentry"}"""),
            2 => Answer(NoFilters),
            3 => Answer("Found Alice."),
            _ => throw new InvalidOperationException()
        });
        var search = new RecordingTool("search_task_masters", """[{"id":"1","name":"Alice"}]""");

        var response = await Service(model, new RecordingJev(_ => throw new InvalidOperationException()),
            search, semanticProvider: CategorySemanticProviders.ChatModel).ChatAsync(
                new ChatRequest { Message = "Find a carpenter" }, Context, CancellationToken.None);

        Assert.Equal("carpentry", search.Arguments!["category"]);
        Assert.Equal(["search_task_masters"], response.Sources);
    }

    [Fact]
    public async Task MissingCatalog_BlocksSearchButAllowsIndependentTools()
    {
        var model = new RecordingChat(round => round == 1
            ? Calls("book", "get_bookings")
            : Answer("Your booking."));
        var jev = new RecordingJev(_ => throw new InvalidOperationException());
        var search = new RecordingTool("search_task_masters", "[]");
        var booking = new RecordingTool("get_bookings", "{}");
        var store = new CategoryCatalogSnapshotStore(
            Microsoft.Extensions.Options.Options.Create(ResolutionOptions()), TimeProvider.System);

        var response = await Service(model, jev, search, booking, snapshots: store).ChatAsync(
            new ChatRequest { Message = "My bookings" }, Context, CancellationToken.None);

        Assert.Equal(["get_bookings"], response.Sources);
        Assert.Equal(0, jev.Calls);
        Assert.Equal(0, search.Calls);
        Assert.Equal(1, booking.Calls);
    }

    [Fact]
    public async Task ExpiredCatalog_BlocksNewCategorySearch()
    {
        var model = new RecordingChat(_ => Calls("search", "search_task_masters"));
        var search = new RecordingTool("search_task_masters", "[]");
        var jev = new RecordingJev(_ => throw new InvalidOperationException());
        var store = new CategoryCatalogSnapshotStore(
            Options.Create(ResolutionOptions()), TimeProvider.System);
        store.Publish(new CategoryCatalogSnapshot(
            1, Version,
            [new CategoryCatalogItem("carpentry", "Carpentry", "Repair wood", [])],
            "{}",
            DateTimeOffset.UtcNow.AddMinutes(-6)));

        var response = await Service(model, jev, search, snapshots: store).ChatAsync(
            new ChatRequest { Message = "Find a carpenter" }, Context, CancellationToken.None);

        Assert.Equal(0, search.Calls);
        Assert.Equal(0, jev.Calls);
        Assert.Empty(response.Sources);
        Assert.Contains("temporarily unavailable", response.Answer);
    }

    [Theory]
    [InlineData("""{"hard":{"maxRate":{"value":50,"unit":"usd_per_hour","evidence":"under $50 total"}},"soft":[],"unsupportedHard":[]}""")]
    [InlineData("""{"hard":{"minRating":{"value":6,"unit":"rating_0_5","evidence":"rated 6 stars"}},"soft":[],"unsupportedHard":[]}""")]
    [InlineData("""{"hard":{"location":{"value":"Chicago, ZZ","unit":"place","evidence":"Chicago, ZZ"}},"soft":[],"unsupportedHard":[]}""")]
    [InlineData("""{"hard":{"maxRate":{"value":50,"unit":"usd_per_hour","evidence":"preferably under $50/hour"}},"soft":[],"unsupportedHard":[]}""")]
    public async Task InvalidFilters_ClarifyWithoutSearching(string extraction)
    {
        var model = new RecordingChat(_ => Answer(extraction));
        var search = new RecordingTool("search_task_masters", "[]");

        var response = await Service(model, new RecordingJev(_ => Resolved("carpentry")), search)
            .ChatAsync(new ChatRequest
            {
                Message = "Find a carpenter under $50 total, rated 6 stars in Chicago, ZZ, preferably under $50/hour"
            }, Context, CancellationToken.None);
        Assert.Equal(0, search.Calls);
        Assert.Empty(response.Sources);
    }

    [Fact]
    public async Task ProductError_DoesNotBecomeProviderEvidence()
    {
        var model = new RecordingChat(_ => Answer(NoFilters));
        var search = new RecordingTool("search_task_masters", """{"error":"category_catalog_unavailable"}""");
        var response = await Service(model, new RecordingJev(_ => Resolved("carpentry")), search)
            .ChatAsync(new ChatRequest { Message = "Find a carpenter" }, Context, CancellationToken.None);
        Assert.Equal(1, search.Calls);
        Assert.Empty(response.Sources);
        Assert.Empty(response.Mentions);
    }

    [Theory]
    [InlineData("Find a carpenter under $50 total")]
    [InlineData("Find a carpenter, I need someone available tomorrow")]
    public async Task OmittedUnsupportedHardRequirement_StillClarifies(string request)
    {
        var model = new RecordingChat(_ => Answer(NoFilters));
        var search = new RecordingTool("search_task_masters", "[]");

        var response = await Service(model, new RecordingJev(_ => Resolved("carpentry")), search)
            .ChatAsync(new ChatRequest { Message = request }, Context, CancellationToken.None);

        Assert.Equal(0, search.Calls);
        Assert.Empty(response.Sources);
    }

    [Fact]
    public async Task OmittedExplicitHourlyHardLimit_ClarifiesInsteadOfWideningSearch()
    {
        var model = new RecordingChat(_ => Answer(NoFilters));
        var search = new RecordingTool("search_task_masters", "[]");

        var response = await Service(model, new RecordingJev(_ => Resolved("carpentry")), search)
            .ChatAsync(new ChatRequest { Message = "Find a carpenter under $50/hour" },
                Context, CancellationToken.None);

        Assert.Equal(0, search.Calls);
        Assert.Empty(response.Sources);
    }

    [Theory]
    [InlineData("Find a carpenter in Chicago, IL")]
    [InlineData("Find a carpenter rated at least 4.5 stars")]
    public async Task OmittedExplicitLocationOrRating_ClarifiesInsteadOfWideningSearch(
        string request)
    {
        var model = new RecordingChat(_ => Answer(NoFilters));
        var search = new RecordingTool("search_task_masters", "[]");

        var response = await Service(model, new RecordingJev(_ => Resolved("carpentry")), search)
            .ChatAsync(new ChatRequest { Message = request }, Context, CancellationToken.None);

        Assert.Equal(0, search.Calls);
        Assert.Empty(response.Sources);
    }

    [Fact]
    public void RepeatedSoftAndHardEvidence_CannotHideLaterHardRequirement()
    {
        const string text = "Preferably under $50/hour, but I must be under $50/hour";
        Assert.True(SearchFilterExtractionService.TryParse(NoFilters, text, [], out var result));
        Assert.True(result!.NeedsClarification);
    }

    [Fact]
    public async Task UnchangedEmptySearch_DoesNotBroadenOrRepeat()
    {
        var model = new RecordingChat(round => round == 1
            ? Answer(NoFilters)
            : Answer("No providers found for that service."));
        var search = new RecordingTool("search_task_masters", "[]");
        var jev = new RecordingJev(_ => Resolved("carpentry"));

        var response = await Service(model, jev, search).ChatAsync(
            new ChatRequest { Message = "Find a carpenter" }, Context, CancellationToken.None);

        Assert.Equal(1, search.Calls);
        Assert.Equal(1, jev.Calls);
        Assert.Equal("No providers found for that service.", response.Answer);
    }

    [Fact]
    public async Task ClarificationReply_PreservesEarlierExplicitHardFilters()
    {
        var model = new RecordingChat(round => round == 1
            ? Answer("""
                {"hard":{"maxRate":{"value":50,"unit":"usd_per_hour","evidence":"under $50/hour"},
                         "location":{"value":"Chicago, IL","unit":"place","evidence":"Chicago, IL"}},
                 "soft":[],"unsupportedHard":[]}
                """)
            : Answer("Found Alice."));
        var search = new RecordingTool("search_task_masters", "[]");
        var response = await Service(model, new RecordingJev(_ => Resolved("carpentry")), search)
            .ChatAsync(new ChatRequest
            {
                Message = "Carpentry, please",
                History =
                [
                    new ChatHistoryMessage
                    {
                        Role = "user",
                        Content = "I need help in Chicago, IL under $50/hour"
                    },
                    new ChatHistoryMessage { Role = "assistant", Content = "Which service?" }
                ]
            }, Context, CancellationToken.None);

        Assert.Equal("Chicago, IL", search.Arguments!["location"]);
        Assert.Equal("50", search.Arguments["maxRate"]);
        Assert.Equal(["search_task_masters"], response.Sources);
    }

    [Theory]
    [InlineData("""{"hard":{"minRate":{"value":60,"unit":"usd_per_hour","evidence":"over $60/hour"},"maxRate":{"value":50,"unit":"usd_per_hour","evidence":"under $50/hour"}},"soft":[],"unsupportedHard":[]}""",
        "over $60/hour and under $50/hour")]
    [InlineData("""{"hard":{"maxRate":{"value":50,"unit":"usd_per_hour","evidence":"under $50/hour"},"unknown":{"value":1,"unit":"usd_per_hour","evidence":"under $50/hour"}},"soft":[],"unsupportedHard":[]}""",
        "under $50/hour")]
    [InlineData("""{"hard":{"maxRate":{"value":50,"unit":"usd_per_hour","evidence":"under $50/hour"},"maxRate":{"value":50,"unit":"usd_per_hour","evidence":"under $50/hour"}},"soft":[],"unsupportedHard":[]}""",
        "under $50/hour")]
    [InlineData("""{"hard":{"maxRate":{"value":50,"unit":"usd_per_hour","evidence":"under $50/hour"}},"soft":[],"unsupportedHard":[]}""",
        "under $50 total")]
    public void FilterEnvelope_RejectsInconsistentOrUngroundedValues(
        string extraction, string request)
    {
        Assert.False(SearchFilterExtractionService.TryParse(
            extraction, request, [], out _));
    }

    [Fact]
    public async Task JevFailure_ReturnsSafeAnswerWithoutSearching()
    {
        var model = new RecordingChat(_ => throw new InvalidOperationException());
        var search = new RecordingTool("search_task_masters", "[]");
        var jev = new RecordingJev(_ =>
            throw new JevDecisionException(JevFailureCategory.InsufficientCredits, 402));

        var response = await Service(model, jev, search).ChatAsync(
            new ChatRequest { Message = "Find a carpenter" }, Context, CancellationToken.None);

        Assert.Contains("temporarily unavailable", response.Answer);
        Assert.Empty(response.Sources);
        Assert.Equal(0, search.Calls);
    }

    private static CategoryResolutionOptions ResolutionOptions(string semanticProvider = CategorySemanticProviders.Jev) =>
        new()
        {
            Mode = CategoryResolutionModes.CatalogLlm,
            SemanticProvider = semanticProvider,
            Jev = new JevOptions
            {
                Policy = new JevDecisionPolicyOptions
                {
                    MinimumConfidence = 0.7,
                    MinimumProbability = 0.7,
                    MinimumMargin = 0.2
                }
            }
        };

    private static JevDecisionResponse Resolved(string choice) => new(
        new JevChoiceAnswer("service_search", 0.9,
            new Dictionary<string, double> { ["service_search"] = 0.9, ["not_applicable"] = 0.1 }),
        new JevChoiceAnswer(choice, 0.9,
            new Dictionary<string, double>
            {
                ["carpentry"] = choice == "carpentry" ? 0.8 : 0.1,
                ["decision:needs_clarification"] = choice == "decision:needs_clarification" ? 0.8 : 0.1,
                ["decision:no_match"] = choice == "decision:no_match" ? 0.8 : 0.1
            }),
        "typesafe/jev-1.13-dated", 10, 5, null);

    private static AiAssistantService Service(
        RecordingChat model,
        RecordingJev jev,
        RecordingTool search,
        RecordingTool? other = null,
        string semanticProvider = CategorySemanticProviders.Jev,
        CategoryCatalogSnapshotStore? snapshots = null,
        ITaskChatModelClient? chatOverride = null)
    {
        var config = Options.Create(ResolutionOptions(semanticProvider));
        var newStore = snapshots is null;
        snapshots ??= new CategoryCatalogSnapshotStore(config, TimeProvider.System);
        if (newStore)
        {
            var categories = ImmutableArray.Create(new CategoryCatalogItem(
                "carpentry", "Carpentry", "Repair wood", ["woodworking"]));
            snapshots.Publish(new CategoryCatalogSnapshot(
                1, Version, categories,
                JsonSerializer.Serialize(new { schemaVersion = 1, catalogVersion = Version, categories }),
                DateTimeOffset.UtcNow));
        }
        var resolver = new Mock<ITaskChatModelResolver>();
        resolver.Setup(r => r.Resolve("chat")).Returns(chatOverride ?? model);
        var prompt = new AssistantPrompt(Options.Create(new PromptOptions
        {
            SystemPrompt = "Only use current tool data for provider claims."
        }));
        var registry = new ToolRegistry(other is null ? [search] : [search, other], snapshots);
        return new AiAssistantService(
            prompt, resolver.Object, registry, NullLogger<AiAssistantService>.Instance,
            config, snapshots, new CategoryDecisionService(config, jev),
            new SearchFilterExtractionService());
    }

    private static ChatModelResponse Answer(string content) =>
        new(new ChatModelMessage(ChatModelRoles.Assistant, content));

    private static ChatModelResponse Calls(string id, string name, string arguments = "{}") =>
        new(new ChatModelMessage(ChatModelRoles.Assistant, null,
            [new ChatModelToolCall(id, name, JsonSerializer.Deserialize<JsonElement>(arguments))]));

    private sealed class RecordingJev(Func<int, JevDecisionResponse> respond) : IJevDecisionClient
    {
        public int Calls { get; private set; }
        public JevDecisionRequest? LastRequest { get; private set; }

        public Task<JevDecisionResponse> DecideAsync(JevDecisionRequest request, CancellationToken token)
        {
            LastRequest = request;
            return Task.FromResult(respond(++Calls));
        }
    }

    private sealed class RecordingChat(Func<int, ChatModelResponse> respond) : ITaskChatModelClient
    {
        public TaskModelProfile Profile { get; } =
            new("chat", ChatModelProviders.Ollama, "qwen3:8b", 10, 60, 1024);
        public List<(IReadOnlyList<ChatModelMessage> Messages, IReadOnlyList<ChatModelTool>? Tools)> Requests { get; } = [];

        public Task<ChatModelResponse> ChatAsync(
            IReadOnlyList<ChatModelMessage> messages,
            IReadOnlyList<ChatModelTool>? tools,
            CancellationToken token)
        {
            Requests.Add((messages.ToArray(), tools));
            return Task.FromResult(respond(Requests.Count));
        }
    }

    private sealed class RecordingTool(string name, string result) : IToolDefinition
    {
        public string Name => name;
        public string Description => "Test tool";
        public object ParametersSchema => new { type = "object" };
        public int Calls { get; private set; }
        public IReadOnlyDictionary<string, string>? Arguments { get; private set; }

        public Task<string> ExecuteAsync(
            ToolExecutionContext context,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            Calls++;
            Arguments = arguments;
            return Task.FromResult(result);
        }
    }

    private sealed class InlineSearchHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"model":"qwen3:8b","done":true,
                         "message":{"role":"assistant",
                           "content":"{\"name\":\"search_task_masters\",\"parameters\":{\"category\":\"gardening\"}}"}}
                        """,
                        Encoding.UTF8, "application/json")
                });
    }
}
