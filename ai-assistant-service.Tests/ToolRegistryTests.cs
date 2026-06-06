using System.Text.Json;
using ai_assistant_service.Services.Tools;
using Xunit;

namespace ai_assistant_service.Tests;

public class ToolRegistryTests
{
    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsNotRegisteredMessage()
    {
        var registry = new ToolRegistry(Array.Empty<IToolDefinition>());

        var result = await registry.ExecuteAsync(
            "does_not_exist",
            new Dictionary<string, JsonElement>(),
            CancellationToken.None);

        Assert.Contains("not registered", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_FlattensJsonElementsToStrings_BeforePassingToTool()
    {
        var fake = new FakeTool();
        var registry = new ToolRegistry(new IToolDefinition[] { fake });

        using var doc = JsonDocument.Parse(
            "{\"category\": \"Pet Care\", \"maxRate\": 25, \"limit\": 10}");
        var args = doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value);

        var result = await registry.ExecuteAsync(fake.Name, args, CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal("Pet Care", fake.LastArgs!["category"]);
        Assert.Equal("25", fake.LastArgs!["maxRate"]);
        Assert.Equal("10", fake.LastArgs!["limit"]);
    }

    [Fact]
    public void Register_AddsToolAtRuntime_AndAllExposesIt()
    {
        var registry = new ToolRegistry(Array.Empty<IToolDefinition>());
        var tool = new FakeTool();

        registry.Register(tool);

        Assert.Same(tool, registry.Get("fake_tool"));
        Assert.Single(registry.All);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        var registry = new ToolRegistry(new IToolDefinition[] { new FakeTool() });

        Assert.NotNull(registry.Get("FAKE_TOOL"));
        Assert.NotNull(registry.Get("fake_tool"));
    }

    private sealed class FakeTool : IToolDefinition
    {
        public string Name => "fake_tool";
        public string Description => "A fake tool used for unit tests.";
        public object ParametersSchema => new { };
        public IReadOnlyDictionary<string, string>? LastArgs { get; private set; }

        public Task<string> ExecuteAsync(
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            LastArgs = arguments;
            return Task.FromResult("ok");
        }
    }
}
