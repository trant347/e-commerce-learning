using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services;
using ai_assistant_service.Services.ChatModels;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Mcp;
using Microsoft.Extensions.Options;

if (args.Length != 6 || args[0] is not ("tuning" or "heldout")
    || !int.TryParse(args[1], out var repeats) || repeats is < 1 or > 3
    || !int.TryParse(args[2], out var maximumCases) || maximumCases is < 1 or > 60)
{
    Console.Error.WriteLine("Usage: OllamaCategoryEvaluationRunner tuning|heldout repeats(1-3) maxCases(1-60) catalog.json cases.jsonl output.jsonl");
    return 2;
}

var catalogText = await File.ReadAllTextAsync(args[3]);
using var catalog = JsonDocument.Parse(catalogText);
var root = catalog.RootElement;
var version = root.GetProperty("catalogVersion").GetString()!;
var categories = root.GetProperty("categories").EnumerateArray()
    .Select(item => new CategoryCatalogItem(
        item.GetProperty("id").GetString()!,
        item.GetProperty("displayName").GetString()!,
        item.GetProperty("description").GetString()!,
        item.GetProperty("aliases").EnumerateArray()
            .Select(alias => alias.GetString()!).ToImmutableArray()))
    .ToImmutableArray();
var snapshot = new CategoryCatalogSnapshot(1, version, categories, catalogText, DateTimeOffset.UtcNow);

using var http = new HttpClient
{
    BaseAddress = new Uri("http://127.0.0.1:11434"),
    Timeout = Timeout.InfiniteTimeSpan
};
using var tags = await http.GetAsync("/api/tags");
tags.EnsureSuccessStatusCode();
using var models = await JsonDocument.ParseAsync(await tags.Content.ReadAsStreamAsync());
if (!models.RootElement.GetProperty("models").EnumerateArray()
    .Any(model => model.GetProperty("name").GetString() == "qwen3:8b"))
{
    Console.Error.WriteLine("Local qwen3:8b model is unavailable.");
    return 2;
}

var cases = (await File.ReadAllLinesAsync(args[4]))
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line => JsonDocument.Parse(line))
    .Where(document => document.RootElement.GetProperty("split").GetString() == args[0])
    .Take(maximumCases)
    .ToArray();
var completed = new HashSet<(string Id, int Repeat)>();
if (File.Exists(args[5]))
{
    foreach (var line in await File.ReadAllLinesAsync(args[5]))
    {
        using var document = JsonDocument.Parse(line);
        var previous = document.RootElement;
        var id = previous.GetProperty("id").GetString()!;
        var repeat = previous.GetProperty("repeat").GetInt32();
        if (previous.GetProperty("catalogVersion").GetString() != version
            || previous.GetProperty("split").GetString() != args[0]
            || !cases.Any(item => item.RootElement.GetProperty("id").GetString() == id)
            || repeat is < 1 or > 3
            || !completed.Add((id, repeat)))
            throw new InvalidOperationException("Existing results are incompatible or duplicated.");
    }
}

var decisionService = new CategoryDecisionService(
    Options.Create(new CategoryResolutionOptions
    {
        Mode = CategoryResolutionModes.CatalogLlm,
        SemanticProvider = CategorySemanticProviders.ChatModel
    }),
    new UnusedJevClient());
var profile = new TaskModelProfile("chat", ChatModelProviders.Ollama, "qwen3:8b", 600, 3000, 1024);

try
{
    foreach (var item in cases)
    {
        var caseRoot = item.RootElement;
        var id = caseRoot.GetProperty("id").GetString()!;
        var messages = caseRoot.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetString()!).ToArray();
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            if (completed.Contains((id, repeat))) continue;

            var chat = new CountingTaskClient(
                new TaskChatModelClient(profile, new OllamaChatModelClient(http)));
            using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var timer = Stopwatch.StartNew();
            CategoryDecision? decision = null;
            string? failure = null;
            try
            {
                decision = await decisionService.ResolveAsync(
                    new ChatRequest { Message = messages[^1] }, messages[..^1],
                    snapshot, chat, () => TimeSpan.FromSeconds(120) - timer.Elapsed,
                    new RequestRoundBudget(5), requestTimeout.Token);
            }
            catch (ChatModelException error)
            {
                failure = error.Category.ToString();
            }
            catch (OperationCanceledException) when (requestTimeout.IsCancellationRequested)
            {
                failure = "Timeout";
            }
            timer.Stop();
            var measured = new
            {
                id,
                split = args[0],
                repeat,
                catalogVersion = version,
                model = profile.Model,
                outcome = decision?.Status.ToString() ?? "Unavailable",
                categoryId = decision?.CategoryId,
                method = decision?.Method ?? "none",
                failure,
                durationMs = timer.Elapsed.TotalMilliseconds,
                costUsd = 0,
                modelCalls = chat.Calls
            };
            await File.AppendAllTextAsync(args[5], JsonSerializer.Serialize(measured) + Environment.NewLine);
            Console.WriteLine($"{id} repeat={repeat} outcome={measured.outcome} durationMs={measured.durationMs:F0}");
            if (failure is not null)
                throw new InvalidOperationException($"Ollama decision failed ({failure}); evaluation stopped.");
        }
    }
}
finally
{
    foreach (var item in cases) item.Dispose();
}

return 0;

sealed class UnusedJevClient : IJevDecisionClient
{
    public Task<JevDecisionResponse> DecideAsync(JevDecisionRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Jev must not be called during an Ollama comparison.");
}

sealed class CountingTaskClient(ITaskChatModelClient inner) : ITaskChatModelClient
{
    public int Calls { get; private set; }
    public TaskModelProfile Profile => inner.Profile;

    public Task<ChatModelResponse> ChatAsync(
        IReadOnlyList<ChatModelMessage> messages,
        IReadOnlyList<ChatModelTool>? tools,
        CancellationToken cancellationToken)
    {
        Calls++;
        return inner.ChatAsync(messages, tools, cancellationToken);
    }
}
