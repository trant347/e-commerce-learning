using ai_assistant_service.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace ai_assistant_service.Tests;

public class CategoryResolutionOptionsValidationTests
{
    [Theory]
    [InlineData("catalog-llm", 0)]
    [InlineData("catalog-llm", 286)]
    [InlineData("catalog-llm", 300)]
    [InlineData("hybrid", 0)]
    [InlineData("hybrid", 286)]
    public void Validator_RejectsInvalidRefreshIntervalInRichModes(
        string mode,
        int intervalSeconds)
    {
        var validator = new CategoryResolutionOptionsValidator(
            Options.Create(new McpDiscoveryOptions
            {
                CategoryRefreshIntervalSeconds = intervalSeconds
            }));

        var result = validator.Validate(
            Options.DefaultName,
            new CategoryResolutionOptions { Mode = mode });

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "McpDiscovery:CategoryRefreshIntervalSeconds",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("catalog-llm", 1)]
    [InlineData("hybrid", 285)]
    public void Validator_AcceptsRichRefreshIntervalWithCallTimeoutHeadroom(
        string mode,
        int intervalSeconds)
    {
        var validator = new CategoryResolutionOptionsValidator(
            Options.Create(new McpDiscoveryOptions
            {
                CategoryRefreshIntervalSeconds = intervalSeconds
            }));

        var result = validator.Validate(
            Options.DefaultName,
            new CategoryResolutionOptions { Mode = mode });

        Assert.True(result.Succeeded);
        Assert.Equal(285, CategoryResolutionOptionsValidator.MaximumRichRefreshIntervalSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Validator_PreservesLegacyRefreshIntervalBehavior(int intervalSeconds)
    {
        var validator = new CategoryResolutionOptionsValidator(
            Options.Create(new McpDiscoveryOptions
            {
                CategoryRefreshIntervalSeconds = intervalSeconds
            }));

        var result = validator.Validate(
            Options.DefaultName,
            new CategoryResolutionOptions { Mode = CategoryResolutionModes.Legacy });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_RejectsUnknownMode()
    {
        var validator = new CategoryResolutionOptionsValidator(
            Options.Create(new McpDiscoveryOptions()));

        var result = validator.Validate(
            Options.DefaultName,
            new CategoryResolutionOptions { Mode = "jev" });

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("CategoryResolution:Mode", StringComparison.Ordinal));
    }
}
