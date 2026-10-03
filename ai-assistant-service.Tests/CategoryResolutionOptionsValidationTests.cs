using ai_assistant_service.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
            }), Options.Create(new OpenRouterOptions()));

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
            }), Options.Create(new OpenRouterOptions()));

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
            }), Options.Create(new OpenRouterOptions()));

        var result = validator.Validate(
            Options.DefaultName,
            new CategoryResolutionOptions { Mode = CategoryResolutionModes.Legacy });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_RejectsUnknownMode()
    {
        var validator = new CategoryResolutionOptionsValidator(
            Options.Create(new McpDiscoveryOptions()), Options.Create(new OpenRouterOptions()));

        var result = validator.Validate(
            Options.DefaultName,
            new CategoryResolutionOptions { Mode = "jev" });

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("CategoryResolution:Mode", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_LegacyModeDoesNotRequireJevCredentials()
    {
        var result = Validate(new CategoryResolutionOptions
        {
            Mode = CategoryResolutionModes.Legacy,
            SemanticProvider = CategorySemanticProviders.Jev
        });
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    public void Validator_RejectsUnknownSemanticProvider(string provider)
    {
        var result = Validate(new CategoryResolutionOptions { SemanticProvider = provider });
        var failures = Assert.IsAssignableFrom<IEnumerable<string>>(result.Failures);
        Assert.Contains(failures, failure => failure.Contains("SemanticProvider", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RequiresAllEnabledJevSettingsAndSharedKey()
    {
        var result = Validate(new CategoryResolutionOptions
        {
            Mode = CategoryResolutionModes.CatalogLlm,
            SemanticProvider = CategorySemanticProviders.Jev
        });

        Assert.True(result.Failed);
        var failures = Assert.IsAssignableFrom<IEnumerable<string>>(result.Failures);
        foreach (var setting in new[]
        {
            "Jev:Model", "Jev:Endpoint", "Jev:TimeoutSeconds",
            "OpenRouter:ApiKey", "Policy:Version", "Policy:EvaluatedModel",
            "Policy:EvaluatedCatalogVersion", "Policy:MinimumConfidence",
            "Policy:MinimumProbability", "Policy:MinimumMargin", "Policy:DistributionTolerance"
        })
            Assert.Contains(failures, failure => failure.Contains(setting, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void Validator_BoundsDecisionTimeout(int seconds, bool valid)
    {
        var options = EnabledOptions();
        options.Jev.TimeoutSeconds = seconds;
        Assert.Equal(valid, Validate(options, "fake-key").Succeeded);
    }

    [Theory]
    [InlineData(-0.01, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(1.01, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Validator_BoundsAcceptanceThresholds(double threshold, bool valid)
    {
        var options = EnabledOptions();
        options.Jev.Policy.MinimumConfidence = threshold;
        Assert.Equal(valid, Validate(options, "fake-key").Succeeded);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0.001, true)]
    [InlineData(0.01, true)]
    [InlineData(0.01001, false)]
    [InlineData(double.NaN, false)]
    public void Validator_BoundsDistributionTolerance(double tolerance, bool valid)
    {
        var options = EnabledOptions();
        options.Jev.Policy.DistributionTolerance = tolerance;
        Assert.Equal(valid, Validate(options, "fake-key").Succeeded);
    }

    [Theory]
    [InlineData("http://openrouter.ai/api/alpha/decisions")]
    [InlineData("https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://user:pass@openrouter.ai/api/alpha/decisions")]
    [InlineData("https://openrouter.ai/api/alpha/decisions?secret=foo")]
    public void Validator_RejectsUnsafeOrWrongEndpoint(string endpoint)
    {
        var options = EnabledOptions();
        options.Jev.Endpoint = endpoint;
        var failures = Assert.IsAssignableFrom<IEnumerable<string>>(Validate(options, "fake-key").Failures);
        Assert.Contains(failures, failure => failure.Contains("Jev:Endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RequiresEvaluatedModelToMatchRequestedModel()
    {
        var options = EnabledOptions();
        options.Jev.Policy.EvaluatedModel = "other/model";
        Assert.False(Validate(options, "fake-key").Succeeded);
    }

    [Fact]
    public void Validator_AcceptsFullyConfiguredJevAndOllamaChat()
    {
        Assert.True(Validate(EnabledOptions(), "fake-key").Succeeded);
    }

    [Fact]
    public void HostStartup_FailsSafelyWhenJevEnabledWithoutRequiredSettings()
    {
        using var factory = new JevStartupFactory();
        var failure = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Jev:Model", failure.Message, StringComparison.Ordinal);
        Assert.Contains("OpenRouter:ApiKey", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("JwtSecret", failure.Message, StringComparison.Ordinal);
    }

    private sealed class JevStartupFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JwtSettings:Secret", "test-jwt-secret-that-is-at-least-32-bytes");
            builder.UseSetting("CategoryResolution:Mode", CategoryResolutionModes.CatalogLlm);
            builder.UseSetting("CategoryResolution:SemanticProvider", CategorySemanticProviders.Jev);
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }

    private static ValidateOptionsResult Validate(CategoryResolutionOptions options, string? key = null) =>
        new CategoryResolutionOptionsValidator(
            Options.Create(new McpDiscoveryOptions()),
            Options.Create(new OpenRouterOptions { ApiKey = key }))
            .Validate(Options.DefaultName, options);

    private static CategoryResolutionOptions EnabledOptions() => new()
    {
        Mode = CategoryResolutionModes.CatalogLlm,
        SemanticProvider = CategorySemanticProviders.Jev,
        Jev = new JevOptions
        {
            Model = "typesafe/jev-1.13",
            Endpoint = "https://openrouter.ai/api/alpha/decisions",
            TimeoutSeconds = 30,
            Policy = new JevDecisionPolicyOptions
            {
                Version = "reviewed-v1",
                EvaluatedModel = "typesafe/jev-1.13",
                EvaluatedCatalogVersion =
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                MinimumConfidence = 0.8,
                MinimumProbability = 0.8,
                MinimumMargin = 0.2,
                DistributionTolerance = 0.001
            }
        }
    };
}
