using System.Text.Json;
using ai_assistant_service.Services.Mcp;
using ai_assistant_service.Services.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ai_assistant_service.Tests;

public class McpCategoryEnumRefresherTests
{
    [Fact]
    public async Task RefreshAsync_AppliesCategoriesFromGetCategories()
    {
        var (refresher, search, categories) = CreateRefresher("[\"carpentry\",\"plumbing\"]");

        var result = await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Updated, result);
        Assert.Equal(["carpentry", "plumbing"], search.AppliedEnum);
        Assert.Equal(["carpentry", "plumbing"], refresher.AppliedCategories);
        Assert.Equal("system:mcp-discovery", categories.LastContext?.Actor);
    }

    [Fact]
    public async Task RefreshAsync_NewCategoryIsAppliedWithoutRestart()
    {
        var (refresher, search, categories) = CreateRefresher("[\"carpentry\"]");
        await refresher.RefreshAsync(CancellationToken.None);

        categories.Result = "[\"appliance-repair\",\"carpentry\"]";
        var result = await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Updated, result);
        Assert.Equal(["appliance-repair", "carpentry"], search.AppliedEnum);
        Assert.Equal(2, search.SetCount);
    }

    [Fact]
    public async Task RefreshAsync_UnchangedCatalogDoesNotRebuildSchema()
    {
        var (refresher, search, _) = CreateRefresher("[\"carpentry\"]");
        await refresher.RefreshAsync(CancellationToken.None);

        var result = await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Unchanged, result);
        Assert.Equal(1, search.SetCount);
    }

    [Fact]
    public async Task RefreshAsync_ReappliesEnumWhenSearchToolIsReplacedByReconnect()
    {
        var categories = new FakeCategoriesTool { Result = "[\"carpentry\"]" };
        var registry = new ToolRegistry(new IToolDefinition[] { new FakeSearchTool(), categories });
        var refresher = new McpCategoryEnumRefresher(
            registry,
            NullLogger<McpCategoryEnumRefresher>.Instance);
        await refresher.RefreshAsync(CancellationToken.None);

        var reconnectedSearch = new FakeSearchTool();
        registry.Register(reconnectedSearch);
        var result = await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Unchanged, result);
        Assert.Equal(["carpentry"], reconnectedSearch.AppliedEnum);
    }

    [Fact]
    public async Task RefreshAsync_ReplacedSearchToolKeepsKnownEnumWhenFetchFails()
    {
        var categories = new FakeCategoriesTool { Result = "[\"carpentry\"]" };
        var registry = new ToolRegistry(new IToolDefinition[] { new FakeSearchTool(), categories });
        var refresher = new McpCategoryEnumRefresher(
            registry,
            NullLogger<McpCategoryEnumRefresher>.Instance);
        await refresher.RefreshAsync(CancellationToken.None);

        var reconnectedSearch = new FakeSearchTool();
        registry.Register(reconnectedSearch);
        categories.Exception = new HttpRequestException("product-service unavailable");
        var result = await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Failed, result);
        Assert.Equal(["carpentry"], reconnectedSearch.AppliedEnum);
    }

    [Fact]
    public async Task ApplyKnownCategoriesAsync_SeedsReplacementToolBeforeRegistration()
    {
        var (refresher, _, _) = CreateRefresher("[\"carpentry\",\"plumbing\"]");
        await refresher.RefreshAsync(CancellationToken.None);

        var replacement = new FakeSearchTool();
        await refresher.ApplyKnownCategoriesAsync(replacement, CancellationToken.None);

        Assert.Equal(["carpentry", "plumbing"], replacement.AppliedEnum);
    }

    [Fact]
    public async Task RefreshAsync_StalledCallTimesOutAsFailure()
    {
        var (refresher, search, categories) = CreateRefresher("[\"carpentry\"]");
        await refresher.RefreshAsync(CancellationToken.None);

        categories.Stall = true;
        refresher.CallTimeout = TimeSpan.FromMilliseconds(100);
        var result = await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Failed, result);
        Assert.Equal(["carpentry"], search.AppliedEnum);
    }

    [Fact]
    public async Task RefreshAsync_UnwrapsJsonEncodedStringResult()
    {
        var encoded = JsonSerializer.Serialize("[\"carpentry\",\"tutoring\"]");
        var (refresher, search, _) = CreateRefresher(encoded);

        await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(["carpentry", "tutoring"], search.AppliedEnum);
    }

    [Fact]
    public async Task RefreshAsync_ToolFailureKeepsPreviousCategories()
    {
        var (refresher, search, categories) = CreateRefresher("[\"carpentry\"]");
        await refresher.RefreshAsync(CancellationToken.None);

        categories.Exception = new HttpRequestException("product-service unavailable");
        var result = await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.Failed, result);
        Assert.Equal(["carpentry"], search.AppliedEnum);
        Assert.Equal(["carpentry"], refresher.AppliedCategories);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"error\":\"boom\"}")]
    public async Task RefreshAsync_EmptyOrMalformedResultKeepsPreviousCategories(string badResult)
    {
        var (refresher, search, categories) = CreateRefresher("[\"carpentry\"]");
        await refresher.RefreshAsync(CancellationToken.None);

        categories.Result = badResult;
        var result = await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.NoUsableCategories, result);
        Assert.Equal(["carpentry"], search.AppliedEnum);
        Assert.Equal(1, search.SetCount);
    }

    [Fact]
    public async Task RefreshAsync_MissingToolsIsNoOp()
    {
        var registry = new ToolRegistry(Array.Empty<IToolDefinition>());
        var refresher = new McpCategoryEnumRefresher(
            registry,
            NullLogger<McpCategoryEnumRefresher>.Instance);

        var result = await refresher.RefreshAsync(CancellationToken.None);

        Assert.Equal(CategoryRefreshResult.ToolsUnavailable, result);
        Assert.Empty(refresher.AppliedCategories);
    }

    private static (McpCategoryEnumRefresher Refresher, FakeSearchTool Search, FakeCategoriesTool Categories)
        CreateRefresher(string categoriesResult)
    {
        var search = new FakeSearchTool();
        var categories = new FakeCategoriesTool { Result = categoriesResult };
        var registry = new ToolRegistry(new IToolDefinition[] { search, categories });
        var refresher = new McpCategoryEnumRefresher(
            registry,
            NullLogger<McpCategoryEnumRefresher>.Instance);
        return (refresher, search, categories);
    }

    private sealed class FakeSearchTool : IToolDefinition, IParameterEnumConstrainable
    {
        public string Name => "search_task_masters";
        public string Description => "Fake search tool.";
        public object ParametersSchema => new { };
        public IReadOnlyList<string>? AppliedEnum { get; private set; }
        public int SetCount { get; private set; }

        public void SetParameterEnum(string parameterName, IReadOnlyList<string> values)
        {
            Assert.Equal("category", parameterName);
            AppliedEnum = values.ToArray();
            SetCount++;
        }

        public Task<string> ExecuteAsync(
            ToolExecutionContext executionContext,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken) => Task.FromResult("[]");
    }

    private sealed class FakeCategoriesTool : IToolDefinition
    {
        public string Name => "get_categories";
        public string Description => "Fake categories tool.";
        public object ParametersSchema => new { };
        public string Result { get; set; } = "[]";
        public Exception? Exception { get; set; }
        public bool Stall { get; set; }
        public ToolExecutionContext? LastContext { get; private set; }

        public async Task<string> ExecuteAsync(
            ToolExecutionContext executionContext,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            LastContext = executionContext;
            if (Stall) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Exception is not null) throw Exception;
            return Result;
        }
    }
}
