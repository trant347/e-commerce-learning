using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Mcp;
using Microsoft.Extensions.Options;

if (args.Length != 6 || args[0] is not ("tuning" or "heldout")
    || !int.TryParse(args[1], out var repeats) || repeats is < 1 or > 3
    || !int.TryParse(args[2], out var maximumCases) || maximumCases is < 1 or > 60)
{
    Console.Error.WriteLine("Usage: JevEvaluationRunner tuning|heldout repeats(1-3) maxCases(1-60) catalog.json cases.jsonl output.jsonl");
    return 2;
}

var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("OPENROUTER_API_KEY is required.");
    return 2;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
using var billing = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
billing.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
var catalogText = await File.ReadAllTextAsync(args[3]);
using var frozen = JsonDocument.Parse(catalogText);
var catalogRoot = frozen.RootElement;
var version = catalogRoot.GetProperty("catalogVersion").GetString()!;
var frozenCategories = catalogRoot.GetProperty("categories").EnumerateArray()
    .Select(ParseCategory).OrderBy(category => category.Id, StringComparer.Ordinal).ToArray();
var live = await http.GetFromJsonAsync<List<CategoryEntry>>(
    "http://127.0.0.1:8080/products/categories/metadata");
if (live is null || live.Count != frozenCategories.Length
    || !live.OrderBy(category => category.Id, StringComparer.Ordinal)
        .SequenceEqual(frozenCategories, CategoryEntryComparer.Instance))
{
    Console.Error.WriteLine("Public catalog differs from the frozen evaluation catalog; no paid calls made.");
    return 2;
}

var initialUsage = await GetUsageAsync(billing);
if (initialUsage.Limit is not 2 || initialUsage.Reset is not null
    || !double.IsFinite(initialUsage.Usage) || initialUsage.Usage >= 1.50)
{
    Console.Error.WriteLine("Key must have a $2 lifetime limit, no reset, and under $1.50 usage.");
    return 2;
}

var snapshot = new CategoryCatalogSnapshot(
    1, version,
    frozenCategories.Select(category => new CategoryCatalogItem(
        category.Id, category.DisplayName, category.Description,
        category.Aliases.ToImmutableArray())).ToImmutableArray(),
    catalogText, DateTimeOffset.UtcNow);
var options = Options.Create(new CategoryResolutionOptions
{
    Jev = new JevOptions
    {
        Model = "typesafe/jev-1.13",
        Endpoint = "https://openrouter.ai/api/alpha/decisions",
        TimeoutSeconds = 30,
        Policy = new JevDecisionPolicyOptions
        {
            EvaluatedCatalogVersion = version,
            DistributionTolerance = 0.001
        }
    }
});
var client = new JevDecisionClient(http, options, Options.Create(new OpenRouterOptions { ApiKey = key }));
var cases = (await File.ReadAllLinesAsync(args[4]))
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line => JsonDocument.Parse(line))
    .Where(document => document.RootElement.GetProperty("split").GetString() == args[0])
    .Take(maximumCases)
    .ToArray();
var completed = new HashSet<(string Id, int Repeat)>();
var recordedCost = 0d;
if (File.Exists(args[5]))
{
    foreach (var line in await File.ReadAllLinesAsync(args[5]))
    {
        using var previous = JsonDocument.Parse(line);
        var result = previous.RootElement;
        var id = result.GetProperty("id").GetString()!;
        var repeat = result.GetProperty("repeat").GetInt32();
        if (result.GetProperty("catalogVersion").GetString() != version
            || result.GetProperty("split").GetString() != args[0]
            || !cases.Any(item => item.RootElement.GetProperty("id").GetString() == id)
            || repeat is < 1 or > 3
            || !completed.Add((id, repeat)))
            throw new InvalidOperationException("Existing result file is incompatible or has duplicate runs.");
        if (result.GetProperty("costUsd").ValueKind == JsonValueKind.Number)
            recordedCost += result.GetProperty("costUsd").GetDouble();
    }
}

try
{
    foreach (var item in cases)
    {
        var root = item.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetString()!).ToArray();
        for (var repeat = 1; repeat <= repeats; repeat++)
        {
            if (completed.Contains((root.GetProperty("id").GetString()!, repeat)))
                continue;
            var before = await GetUsageAsync(billing);
            if (before.Limit is not 2 || before.Reset is not null
                || !double.IsFinite(before.Usage) || before.Usage >= 1.50
                || before.Usage < initialUsage.Usage || recordedCost >= 1.50)
                throw new InvalidOperationException("Spending safeguard failed; evaluation stopped.");

            var timer = Stopwatch.StartNew();
            JevDecisionResponse? response = null;
            string? failure = null;
            var repairs = 0;
            try
            {
                response = await client.DecideAsync(
                    new JevDecisionRequest(messages[^1], messages[..^1], snapshot, RemainingTime()),
                    CancellationToken.None);
            }
            catch (JevDecisionException error) when (error.Category == JevFailureCategory.MalformedDecision)
            {
                repairs++;
                try
                {
                    response = await client.DecideAsync(
                        new JevDecisionRequest(messages[^1], messages[..^1], snapshot, RemainingTime()),
                        CancellationToken.None);
                }
                catch (JevDecisionException second) when (second.Category == JevFailureCategory.MalformedDecision)
                {
                    failure = nameof(JevFailureCategory.MalformedDecision);
                }
            }
            timer.Stop();

            var after = await GetUsageAsync(billing);
            if (!double.IsFinite(after.Usage) || after.Usage < before.Usage
                || after.Limit is not 2 || after.Reset is not null
                || (response?.Cost is double value && (!double.IsFinite(value) || value < 0)))
                throw new InvalidOperationException("Spending telemetry changed unexpectedly; evaluation stopped.");
            if (response?.Cost is double cost)
                recordedCost += cost;
            else if (failure is null)
                throw new InvalidOperationException("Provider did not report a measured cost.");

            var measured = new
            {
                id = root.GetProperty("id").GetString(),
                split = args[0],
                repeat,
                catalogVersion = version,
                model = response?.ResolvedModel,
                intent = response is null ? null : Summarize(response.Intent),
                category = response is null ? null : Summarize(response.Category),
                failure,
                durationMs = timer.Elapsed.TotalMilliseconds,
                costUsd = response?.Cost,
                repairAttempts = repairs,
                inputTokens = response?.InputTokens,
                outputTokens = response?.OutputTokens
            };
            await File.AppendAllTextAsync(
                args[5], JsonSerializer.Serialize(measured) + Environment.NewLine);
            Console.WriteLine($"{measured.id} repeat={repeat} outcome={failure ?? "parsed"} usageUsd={after.Usage:F5}");
            if (after.Usage >= 1.50 || recordedCost >= 1.50)
                throw new InvalidOperationException("Spending stop threshold reached.");

            TimeSpan RemainingTime() => TimeSpan.FromSeconds(30) - timer.Elapsed;
        }
    }
}
catch (JevDecisionException error)
{
    Console.Error.WriteLine($"Jev request failed: {error.Category} (HTTP {error.HttpStatusCode}).");
    return 1;
}
finally
{
    foreach (var item in cases) item.Dispose();
}

return 0;

static object Summarize(JevChoiceAnswer answer) => new
{
    answer.Choice,
    answer.Confidence,
    ChosenProbability = answer.Probabilities[answer.Choice],
    RunnerUpProbability = answer.Probabilities
        .Where(pair => pair.Key != answer.Choice)
        .Select(pair => pair.Value).DefaultIfEmpty(0).Max()
};

static CategoryEntry ParseCategory(JsonElement item) => new(
    item.GetProperty("id").GetString()!,
    item.GetProperty("displayName").GetString()!,
    item.GetProperty("description").GetString()!,
    item.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()!).ToArray());

static async Task<KeyUsage> GetUsageAsync(HttpClient client)
{
    // The usage check gates every paid call; wait out rate limits instead of aborting the run.
    for (var attempt = 1; ; attempt++)
    {
        using var response = await client.GetAsync("https://openrouter.ai/api/v1/key");
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < 5)
        {
            await Task.Delay(TimeSpan.FromSeconds(10 * attempt));
            continue;
        }
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var data = body.RootElement.GetProperty("data");
        return new KeyUsage(
            data.GetProperty("limit").GetDouble(),
            data.GetProperty("usage").GetDouble(),
            data.GetProperty("limit_reset").GetString());
    }
}

sealed record CategoryEntry(string Id, string DisplayName, string Description, string[] Aliases);
sealed record KeyUsage(double Limit, double Usage, string? Reset);

sealed class CategoryEntryComparer : IEqualityComparer<CategoryEntry>
{
    public static CategoryEntryComparer Instance { get; } = new();
    public bool Equals(CategoryEntry? left, CategoryEntry? right) =>
        left is not null && right is not null
        && left.Id == right.Id
        && left.DisplayName == right.DisplayName
        && left.Description == right.Description
        && left.Aliases.Order(StringComparer.Ordinal)
            .SequenceEqual(right.Aliases.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    public int GetHashCode(CategoryEntry category) =>
        StringComparer.Ordinal.GetHashCode(category.Id);
}
