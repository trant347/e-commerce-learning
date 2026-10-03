using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Mcp;
using ai_assistant_service.Services.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ai_assistant_service.Tests;

public class CategoryCatalogSnapshotTests
{
    private const string VersionA =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string VersionB =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task RefreshAsync_PublishesRichSnapshotAndEnumFromSameCatalog()
    {
        var fixture = CreateFixture(Catalog(VersionA, "Wood construction.", ["woodworking"]));

        var result = await fixture.Refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Updated, result);
        var snapshot = Assert.IsType<CategoryCatalogSnapshot>(fixture.Snapshots.Current);
        Assert.Equal(VersionA, snapshot.CatalogVersion);
        Assert.Equal("carpentry", Assert.Single(snapshot.CategoryIds));
        Assert.Equal(["carpentry"], fixture.Search.AppliedEnum);
        Assert.Contains("\"description\":\"Wood construction.\"", snapshot.DecisionContext);
    }

    [Fact]
    public async Task RefreshAsync_MetadataOnlyChangePublishesUpdatedSnapshotWithoutRebuildingEnum()
    {
        var fixture = CreateFixture(Catalog(VersionA, "Wood construction.", []));
        await fixture.Refresher.RefreshAsync(CancellationToken.None);

        fixture.Catalog.Result = Catalog(VersionB, "Wood construction and repair.", []);
        var result = await fixture.Refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Updated, result);
        Assert.Equal(VersionB, fixture.Snapshots.Current?.CatalogVersion);
        Assert.Contains("and repair", fixture.Snapshots.Current?.DecisionContext);
        Assert.Equal(1, fixture.Search.SetCount);
    }

    [Fact]
    public async Task RefreshAsync_UnchangedFetchRefreshesSnapshotAge()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-03T12:00:00Z"));
        var fixture = CreateFixture(Catalog(VersionA, "Wood construction.", []), time);
        await fixture.Refresher.RefreshAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(4));
        var result = await fixture.Refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Unchanged, result);
        Assert.Equal(time.GetUtcNow(), fixture.Snapshots.Current?.LastSuccessfulRefreshAt);
        Assert.True(fixture.Snapshots.TryCaptureUsable(out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"error\":\"category_catalog_unavailable\"}")]
    [InlineData("{\"schemaVersion\":1,\"catalogVersion\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"categories\":[]}")]
    public async Task RefreshAsync_InvalidResponseRetainsLastGoodSnapshot(string invalid)
    {
        var fixture = CreateFixture(Catalog(VersionA, "Wood construction.", []));
        await fixture.Refresher.RefreshAsync(CancellationToken.None);

        fixture.Catalog.Result = invalid;
        var result = await fixture.Refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.NoUsableCategories, result);
        Assert.Equal(VersionA, fixture.Snapshots.Current?.CatalogVersion);
        Assert.Equal(["carpentry"], fixture.Search.AppliedEnum);
    }

    [Fact]
    public async Task RefreshAsync_UnwrapsJsonEncodedCatalog()
    {
        var fixture = CreateFixture(JsonSerializer.Serialize(
            Catalog(VersionA, "Wood construction.", ["woodworking"])));

        var result = await fixture.Refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Updated, result);
        Assert.Equal(VersionA, fixture.Snapshots.Current?.CatalogVersion);
    }

    [Fact]
    public async Task RefreshAsync_SameVersionWithDifferentContentRetainsLastGoodSnapshot()
    {
        var fixture = CreateFixture(Catalog(VersionA, "Wood construction.", []));
        await fixture.Refresher.RefreshAsync(CancellationToken.None);

        fixture.Catalog.Result = Catalog(VersionA, "Changed without a new digest.", []);
        var result = await fixture.Refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.NoUsableCategories, result);
        Assert.DoesNotContain("Changed without", fixture.Snapshots.Current?.DecisionContext);
    }

    [Fact]
    public async Task RefreshAsync_RejectsMoreThanOneHundredCategories()
    {
        var categories = Enumerable.Range(0, 101)
            .Select(index => new
            {
                id = $"category-{index}",
                displayName = $"Category {index}",
                description = "Description",
                aliases = Array.Empty<string>()
            });
        var catalog = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            catalogVersion = VersionA,
            categories
        });
        var fixture = CreateFixture(catalog);

        var result = await fixture.Refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.NoUsableCategories, result);
        Assert.Null(fixture.Snapshots.Current);
    }

    [Fact]
    public async Task RefreshAsync_RejectsCatalogOverContextLimit()
    {
        var fixture = CreateFixture(
            Catalog(VersionA, new string('x', 100), []),
            maximumContextChars: 100);

        var result = await fixture.Refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.NoUsableCategories, result);
        Assert.Null(fixture.Snapshots.Current);
    }

    [Fact]
    public async Task RefreshAsync_ReappliesLastSnapshotToReplacementSearchTool()
    {
        var fixture = CreateFixture(Catalog(VersionA, "Wood construction.", []));
        await fixture.Refresher.RefreshAsync(CancellationToken.None);

        var replacement = new FakeSearchTool();
        await fixture.Refresher.ApplyKnownCategoriesAsync(replacement, CancellationToken.None);

        Assert.Equal(["carpentry"], replacement.AppliedEnum);
    }

    [Fact]
    public async Task RefreshAsync_CallerCancellationPropagates()
    {
        var fixture = CreateFixture(Catalog(VersionA, "Wood construction.", []));
        fixture.Catalog.Stall = true;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Refresher.RefreshAsync(cts.Token));
    }

    [Fact]
    public async Task SnapshotBecomesUnusableAfterFiveMinutes()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-03T12:00:00Z"));
        var fixture = CreateFixture(Catalog(VersionA, "Wood construction.", []), time);
        await fixture.Refresher.RefreshAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));

        Assert.False(fixture.Snapshots.TryCaptureUsable(out _, out var reason));
        Assert.Contains("older than five minutes", reason);
        Assert.Equal("degraded", fixture.Snapshots.GetReadiness().Status);
    }

    [Fact]
    public async Task ToolRegistry_BlocksOnlyCategorySearchWhenSnapshotIsUnavailable()
    {
        var options = RichOptions();
        var snapshots = new CategoryCatalogSnapshotStore(options, TimeProvider.System);
        var search = new RecordingTool("search_task_masters");
        var unrelated = new RecordingTool("get_bookings");
        var registry = new ToolRegistry([search, unrelated], snapshots);

        var categoryResult = await registry.ExecuteAsync(
            new ToolExecutionContext("user", [], "correlation"),
            "search_task_masters",
            Arguments(("category", "carpentry")),
            CancellationToken.None);
        var locationOnlyResult = await registry.ExecuteAsync(
            new ToolExecutionContext("user", [], "correlation"),
            "search_task_masters",
            Arguments(("location", "Chicago")),
            CancellationToken.None);
        var unrelatedResult = await registry.ExecuteAsync(
            new ToolExecutionContext("user", [], "correlation"),
            "get_bookings",
            Arguments(),
            CancellationToken.None);

        Assert.Contains("category_catalog_unavailable", categoryResult);
        Assert.Equal("executed", locationOnlyResult);
        Assert.Equal("executed", unrelatedResult);
        Assert.Equal(1, search.ExecutionCount);
        Assert.Equal(1, unrelated.ExecutionCount);
    }

    private static Fixture CreateFixture(
        string catalogResult,
        ManualTimeProvider? timeProvider = null,
        int maximumContextChars = CategoryResolutionOptions.DefaultMaxCatalogContextChars)
    {
        timeProvider ??= new ManualTimeProvider(DateTimeOffset.UtcNow);
        var options = RichOptions(maximumContextChars);
        var snapshots = new CategoryCatalogSnapshotStore(options, timeProvider);
        var search = new FakeSearchTool();
        var catalog = new FakeCatalogTool { Result = catalogResult };
        var registry = new ToolRegistry([search, catalog], snapshots);
        var refresher = new McpCategoryEnumRefresher(
            registry,
            options,
            snapshots,
            timeProvider,
            NullLogger<McpCategoryEnumRefresher>.Instance);
        return new Fixture(refresher, snapshots, search, catalog);
    }

    private static IOptions<CategoryResolutionOptions> RichOptions(
        int maximumContextChars = CategoryResolutionOptions.DefaultMaxCatalogContextChars) =>
        Options.Create(new CategoryResolutionOptions
        {
            Mode = CategoryResolutionModes.CatalogLlm,
            MaxCatalogContextChars = maximumContextChars
        });

    private static string Catalog(string version, string description, string[] aliases) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            catalogVersion = version,
            categories = new[]
            {
                new
                {
                    id = "carpentry",
                    displayName = "Carpentry",
                    description,
                    aliases
                }
            }
        });

    private static IReadOnlyDictionary<string, JsonElement> Arguments(
        params (string Name, string Value)[] values) =>
        values.ToDictionary(
            value => value.Name,
            value => JsonSerializer.SerializeToElement(value.Value));

    private sealed record Fixture(
        McpCategoryEnumRefresher Refresher,
        CategoryCatalogSnapshotStore Snapshots,
        FakeSearchTool Search,
        FakeCatalogTool Catalog);

    private sealed class FakeSearchTool : IToolDefinition, IParameterEnumConstrainable
    {
        public string Name => "search_task_masters";
        public string Description => "Search.";
        public object ParametersSchema => new { };
        public IReadOnlyList<string>? AppliedEnum { get; private set; }
        public int SetCount { get; private set; }

        public void SetParameterEnum(string parameterName, IReadOnlyList<string> values)
        {
            AppliedEnum = values.ToArray();
            SetCount++;
        }

        public Task<string> ExecuteAsync(
            ToolExecutionContext executionContext,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken) => Task.FromResult("[]");
    }

    private sealed class FakeCatalogTool : IToolDefinition
    {
        public string Name => "get_category_catalog";
        public string Description => "Catalog.";
        public object ParametersSchema => new { };
        public string Result { get; set; } = string.Empty;
        public bool Stall { get; set; }

        public async Task<string> ExecuteAsync(
            ToolExecutionContext executionContext,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            if (Stall)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Result;
        }
    }

    private sealed class RecordingTool(string name) : IToolDefinition
    {
        public string Name { get; } = name;
        public string Description => "Recording tool.";
        public object ParametersSchema => new { };
        public int ExecutionCount { get; private set; }

        public Task<string> ExecuteAsync(
            ToolExecutionContext executionContext,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult("executed");
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
