using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Http.Headers;
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

if (args.Length != 7
    || !int.TryParse(args[0], out var repeats) || repeats is < 1 or > 3
    || !int.TryParse(args[1], out var maximumCases) || maximumCases is < 1 or > 60
    || !double.TryParse(args[5], out var additionalSpendLimit)
    || !double.IsFinite(additionalSpendLimit) || additionalSpendLimit <= 0)
{
    Console.Error.WriteLine(
        "Usage: JevEndToEndEvaluationRunner repeats(1-3) maxCases(1-60) catalog.json cases.jsonl policy.json additionalSpendLimitUsd output.jsonl");
    return 2;
}

var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("OPENROUTER_API_KEY is required.");
    return 2;
}

var catalogText = await File.ReadAllTextAsync(args[2]);
using var catalogDocument = JsonDocument.Parse(catalogText);
var catalogRoot = catalogDocument.RootElement;
var catalogVersion = catalogRoot.GetProperty("catalogVersion").GetString()!;
var categories = catalogRoot.GetProperty("categories").EnumerateArray()
    .Select(item => new CategoryCatalogItem(
        item.GetProperty("id").GetString()!,
        item.GetProperty("displayName").GetString()!,
        item.GetProperty("description").GetString()!,
        item.GetProperty("aliases").EnumerateArray()
            .Select(alias => alias.GetString()!).ToImmutableArray()))
    .ToImmutableArray();
var snapshot = new CategoryCatalogSnapshot(
    1, catalogVersion, categories, catalogText, DateTimeOffset.UtcNow);

using var policyDocument = JsonDocument.Parse(await File.ReadAllTextAsync(args[4]));
var policyRoot = policyDocument.RootElement;
var policyVersion = policyRoot.GetProperty("version").GetString()!;
var evaluatedCatalogVersion = policyRoot.GetProperty("evaluatedCatalogVersion").GetString()!;
if (!string.Equals(catalogVersion, evaluatedCatalogVersion, StringComparison.Ordinal))
{
    Console.Error.WriteLine("The policy and frozen catalog versions differ; no paid calls made.");
    return 2;
}

var cases = (await File.ReadAllLinesAsync(args[3]))
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line => JsonDocument.Parse(line))
    .Where(document => document.RootElement.GetProperty("split").GetString() == "heldout")
    .Take(maximumCases)
    .ToArray();
if (cases.Length != maximumCases)
{
    Console.Error.WriteLine($"Expected {maximumCases} held-out cases; found {cases.Length}.");
    return 2;
}

using var ollamaHttp = new HttpClient
{
    BaseAddress = new Uri("http://127.0.0.1:11434"),
    Timeout = Timeout.InfiniteTimeSpan
};
using (var tags = await ollamaHttp.GetAsync("/api/tags"))
{
    tags.EnsureSuccessStatusCode();
    using var models = await JsonDocument.ParseAsync(await tags.Content.ReadAsStreamAsync());
    if (!models.RootElement.GetProperty("models").EnumerateArray()
        .Any(model => model.GetProperty("name").GetString() == "qwen3:8b"))
    {
        Console.Error.WriteLine("Local qwen3:8b model is unavailable.");
        return 2;
    }
}

using var jevHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
using var billingHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
billingHttp.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", key.Trim());
var initialUsage = await GetUsageAsync(billingHttp);
if (initialUsage.Limit is not 2 || initialUsage.Reset is not null
    || !double.IsFinite(initialUsage.Usage)
    || initialUsage.Usage + additionalSpendLimit > initialUsage.Limit)
{
    Console.Error.WriteLine(
        "The evaluation key must have a $2 lifetime limit, no reset, and enough remaining capacity for the approved incremental cap.");
    return 2;
}

var completed = new HashSet<(string Mode, string Id, int Repeat)>();
if (File.Exists(args[6]))
{
    foreach (var line in await File.ReadAllLinesAsync(args[6]))
    {
        using var prior = JsonDocument.Parse(line);
        var root = prior.RootElement;
        var keyTuple = (
            root.GetProperty("mode").GetString()!,
            root.GetProperty("id").GetString()!,
            root.GetProperty("repeat").GetInt32());
        if (root.GetProperty("catalogVersion").GetString() != catalogVersion
            || root.GetProperty("policyVersion").GetString() != policyVersion
            || keyTuple.Item1 is not ("legacy" or "catalog-llm-jev")
            || keyTuple.Item3 is < 1 or > 3
            || !cases.Any(item => item.RootElement.GetProperty("id").GetString() == keyTuple.Item2)
            || !completed.Add(keyTuple))
        {
            throw new InvalidOperationException(
                "Existing result file is incompatible or contains duplicate runs.");
        }
    }
}

var profile = new TaskModelProfile(
    "chat", ChatModelProviders.Ollama, "qwen3:8b", 600, 3000, 1024);
var prompt = new AssistantPrompt(Options.Create(LoadPromptOptions()));

try
{
    foreach (var mode in new[] { "legacy", "catalog-llm-jev" })
    {
        foreach (var item in cases)
        {
            var root = item.RootElement;
            var id = root.GetProperty("id").GetString()!;
            var messages = root.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetString()!).ToArray();
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                if (completed.Contains((mode, id, repeat))) continue;
                if (mode == "catalog-llm-jev")
                    await EnsureSpendAvailableAsync();

                var search = new FixtureSearchTool(categories);
                var chat = new CountingTaskClient(
                    new TaskChatModelClient(profile, new OllamaChatModelClient(ollamaHttp)));
                var resolver = new FixedResolver(chat);
                var options = CreateOptions(mode);
                var snapshots = new CategoryCatalogSnapshotStore(options, TimeProvider.System);
                snapshots.Publish(snapshot with { LastSuccessfulRefreshAt = DateTimeOffset.UtcNow });
                var registry = new ToolRegistry([search], snapshots);
                var jev = mode == "catalog-llm-jev"
                    ? new CountingJevClient(
                        new JevDecisionClient(
                            jevHttp, options,
                            Options.Create(new OpenRouterOptions { ApiKey = key.Trim() })))
                    : new CountingJevClient(new UnusedJevClient());
                var assistant = new AiAssistantService(
                    prompt, resolver, registry, NullLogger<AiAssistantService>.Instance,
                    options, snapshots, new CategoryDecisionService(options, jev),
                    new SearchFilterExtractionService());
                var history = messages[..^1]
                    .Select(message => new ChatHistoryMessage
                    {
                        Role = ChatModelRoles.User,
                        Content = message
                    }).ToList();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                var timer = Stopwatch.StartNew();
                var response = await assistant.ChatAsync(
                    new ChatRequest { Message = messages[^1], History = history },
                    new ToolExecutionContext("evaluation", [], $"jev09-{id}-{repeat}"),
                    timeout.Token);
                timer.Stop();

                var outcome = ClassifyOutcome(response, search, chat, mode);
                var categoryId = outcome == CategoryDecisionStatus.Resolved
                    ? search.LastArguments?.GetValueOrDefault("category")
                    : null;
                var expectedProvider = categoryId is null
                    ? null
                    : search.ProviderFor(categoryId);
                var grounded = search.Calls == 0
                    ? response.Sources.Count == 0 && response.Mentions.Count == 0
                    : expectedProvider is not null
                      && response.Sources.SequenceEqual([search.Name], StringComparer.Ordinal)
                      && response.Mentions.Count == 1
                      && response.Mentions[0].Id == expectedProvider.Id
                      && response.Mentions[0].Name == expectedProvider.Name;

                var afterUsage = mode == "catalog-llm-jev"
                    ? await GetUsageAsync(billingHttp)
                    : initialUsage;
                if (mode == "catalog-llm-jev"
                    && (!double.IsFinite(afterUsage.Usage)
                        || afterUsage.Usage < initialUsage.Usage
                        || afterUsage.Usage - initialUsage.Usage > additionalSpendLimit))
                {
                    throw new InvalidOperationException(
                        "Approved incremental spending limit reached; evaluation stopped.");
                }

                var measured = new
                {
                    id,
                    mode,
                    repeat,
                    catalogVersion,
                    policyVersion,
                    outcome = outcome.ToString(),
                    categoryId,
                    method = mode == "catalog-llm-jev"
                        ? (outcome == CategoryDecisionStatus.Resolved ? "jev" : "none")
                        : (outcome == CategoryDecisionStatus.Resolved ? "llm" : "none"),
                    durationMs = timer.Elapsed.TotalMilliseconds,
                    costUsd = mode == "catalog-llm-jev" ? jev.CostUsd : 0,
                    modelCalls = chat.Calls + jev.Calls,
                    ollamaCalls = chat.Calls,
                    jevCalls = jev.Calls,
                    searchCalls = search.Calls,
                    grounded,
                    sourceCount = response.Sources.Count,
                    mentionCount = response.Mentions.Count
                };
                await File.AppendAllTextAsync(
                    args[6], JsonSerializer.Serialize(measured) + Environment.NewLine);
                completed.Add((mode, id, repeat));
                Console.WriteLine(
                    $"{mode} {id} repeat={repeat} outcome={measured.outcome} durationMs={measured.durationMs:F0} grounded={grounded} additionalUsageUsd={Math.Max(0, afterUsage.Usage - initialUsage.Usage):F5}");
            }
        }
    }
}
finally
{
    foreach (var item in cases) item.Dispose();
}

return 0;

IOptions<CategoryResolutionOptions> CreateOptions(string mode) =>
    Options.Create(new CategoryResolutionOptions
    {
        Mode = mode == "catalog-llm-jev"
            ? CategoryResolutionModes.CatalogLlm
            : CategoryResolutionModes.Legacy,
        SemanticProvider = mode == "catalog-llm-jev"
            ? CategorySemanticProviders.Jev
            : CategorySemanticProviders.ChatModel,
        Jev = new JevOptions
        {
            Model = policyRoot.GetProperty("evaluatedModel").GetString(),
            Endpoint = "https://openrouter.ai/api/alpha/decisions",
            TimeoutSeconds = 30,
            Policy = new JevDecisionPolicyOptions
            {
                Version = policyVersion,
                EvaluatedModel = policyRoot.GetProperty("evaluatedModel").GetString(),
                EvaluatedCatalogVersion = evaluatedCatalogVersion,
                MinimumConfidence = policyRoot.GetProperty("minimumConfidence").GetDouble(),
                MinimumProbability = policyRoot.GetProperty("minimumProbability").GetDouble(),
                MinimumMargin = policyRoot.GetProperty("minimumMargin").GetDouble(),
                DistributionTolerance = policyRoot.GetProperty("distributionTolerance").GetDouble()
            }
        }
    });

static PromptOptions LoadPromptOptions()
{
    using var document = JsonDocument.Parse(
        File.ReadAllText("ai-assistant-service/appsettings.json"));
    return JsonSerializer.Deserialize<PromptOptions>(
        document.RootElement.GetProperty("PromptOptions").GetRawText(),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException(
            "The production PromptOptions section could not be loaded.");
}

async Task EnsureSpendAvailableAsync()
{
    var usage = await GetUsageAsync(billingHttp);
    if (usage.Limit is not 2 || usage.Reset is not null
        || !double.IsFinite(usage.Usage) || usage.Usage < initialUsage.Usage
        || usage.Usage - initialUsage.Usage >= additionalSpendLimit)
    {
        throw new InvalidOperationException(
            "Approved incremental spending limit reached; evaluation stopped.");
    }
}

static CategoryDecisionStatus ClassifyOutcome(
    ChatResponse response,
    FixtureSearchTool search,
    CountingTaskClient chat,
    string mode)
{
    if (search.Calls > 0) return CategoryDecisionStatus.Resolved;
    if (response.Answer.StartsWith(
            "Which single marketplace service", StringComparison.Ordinal))
        return CategoryDecisionStatus.NeedsClarification;
    if (response.Answer.StartsWith(
            "I couldn't match that request", StringComparison.Ordinal))
        return CategoryDecisionStatus.NoMatch;
    var categoryCalls = mode == "catalog-llm-jev" ? 0 : 1;
    return chat.Calls > categoryCalls
        ? CategoryDecisionStatus.NotApplicable
        : CategoryDecisionStatus.NeedsClarification;
}

static async Task<KeyUsage> GetUsageAsync(HttpClient client)
{
    for (var attempt = 1; ; attempt++)
    {
        using var response = await client.GetAsync("https://openrouter.ai/api/v1/key");
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 5)
        {
            await Task.Delay(TimeSpan.FromSeconds(10 * attempt));
            continue;
        }
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        var data = body.RootElement.GetProperty("data");
        return new KeyUsage(
            data.GetProperty("limit").GetDouble(),
            data.GetProperty("usage").GetDouble(),
            data.GetProperty("limit_reset").GetString());
    }
}

sealed record KeyUsage(double Limit, double Usage, string? Reset);
sealed record FixtureProvider(string Id, string Name);

sealed class FixedResolver(ITaskChatModelClient client) : ITaskChatModelResolver
{
    public ITaskChatModelClient Resolve(string taskId) => client;
}

sealed class CountingTaskClient(ITaskChatModelClient inner) : ITaskChatModelClient
{
    public int Calls { get; private set; }
    public TaskModelProfile Profile => inner.Profile;

    public async Task<ChatModelResponse> ChatAsync(
        IReadOnlyList<ChatModelMessage> messages,
        IReadOnlyList<ChatModelTool>? tools,
        CancellationToken cancellationToken)
    {
        Calls++;
        return await inner.ChatAsync(messages, tools, cancellationToken);
    }
}

sealed class CountingJevClient(IJevDecisionClient inner) : IJevDecisionClient
{
    public int Calls { get; private set; }
    public double CostUsd { get; private set; }

    public async Task<JevDecisionResponse> DecideAsync(
        JevDecisionRequest request,
        CancellationToken cancellationToken)
    {
        Calls++;
        var response = await inner.DecideAsync(request, cancellationToken);
        CostUsd += response.Cost ?? 0;
        return response;
    }
}

sealed class UnusedJevClient : IJevDecisionClient
{
    public Task<JevDecisionResponse> DecideAsync(
        JevDecisionRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Jev must not run in the legacy baseline.");
}

sealed class FixtureSearchTool : IToolDefinition, IParameterEnumConstrainable
{
    private readonly IReadOnlyDictionary<string, CategoryCatalogItem> _categories;

    public FixtureSearchTool(IEnumerable<CategoryCatalogItem> categories)
    {
        _categories = categories.ToDictionary(category => category.Id, StringComparer.Ordinal);
        ParametersSchema = CreateSchema(_categories.Keys);
    }

    public string Name => "search_task_masters";
    public string Description => "Search TaskMasters by category and supported filters.";
    public object ParametersSchema { get; private set; }
    public int Calls { get; private set; }
    public IReadOnlyDictionary<string, string>? LastArguments { get; private set; }

    public void SetParameterEnum(string parameterName, IReadOnlyList<string> values) =>
        ParametersSchema = CreateSchema(values);

    public FixtureProvider? ProviderFor(string categoryId) =>
        _categories.TryGetValue(categoryId, out var category)
            ? new FixtureProvider(
                $"fixture-{category.Id}",
                $"{category.DisplayName} Fixture Provider")
            : null;

    public Task<string> ExecuteAsync(
        ToolExecutionContext context,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastArguments = new Dictionary<string, string>(arguments);
        if (!arguments.TryGetValue("category", out var categoryId)
            || ProviderFor(categoryId) is not { } provider)
            return Task.FromResult("""{"error":"invalid_category"}""");
        return Task.FromResult(JsonSerializer.Serialize(new[]
        {
            new
            {
                id = provider.Id,
                name = provider.Name,
                description = _categories[categoryId].Description,
                location = arguments.GetValueOrDefault("location", "Chicago, IL"),
                hourlyRateUsd = 50,
                rating = 4.8,
                jobCategories = new[] { categoryId }
            }
        }));
    }

    private static object CreateSchema(IEnumerable<string> categoryIds) => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["category"] = new
            {
                type = "string",
                @enum = categoryIds.Order(StringComparer.Ordinal).ToArray()
            },
            ["location"] = new { type = "string" },
            ["minRate"] = new { type = "number" },
            ["maxRate"] = new { type = "number" },
            ["minRating"] = new { type = "number" }
        },
        required = new[] { "category" }
    };
}
