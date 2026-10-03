using ai_assistant_service.Services.Mcp;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Configuration;

/// <summary>Configures category-resolution rollout mode and rich-catalog limits.</summary>
public sealed class CategoryResolutionOptions
{
    public const string SectionName = "CategoryResolution";
    public const int DefaultMaxCatalogContextChars = 12_000;

    public string Mode { get; set; } = CategoryResolutionModes.Legacy;
    public string SemanticProvider { get; set; } = CategorySemanticProviders.ChatModel;
    public int MaxCatalogContextChars { get; set; } = DefaultMaxCatalogContextChars;
    public JevOptions Jev { get; set; } = new();
}

/// <summary>Supported category decision engines.</summary>
public static class CategorySemanticProviders
{
    public const string ChatModel = "ChatModel";
    public const string Jev = "Jev";
}

/// <summary>Explicit Decisions API configuration, independent of the chat profile.</summary>
public sealed class JevOptions
{
    public string? Model { get; set; }
    public string? Endpoint { get; set; }
    public int? TimeoutSeconds { get; set; }
    public JevDecisionPolicyOptions Policy { get; set; } = new();
}

/// <summary>Versioned, evaluated acceptance thresholds for Jev decisions.</summary>
public sealed class JevDecisionPolicyOptions
{
    public string? Version { get; set; }
    public string? EvaluatedModel { get; set; }
    public string? EvaluatedCatalogVersion { get; set; }
    public double? MinimumConfidence { get; set; }
    public double? MinimumProbability { get; set; }
    public double? MinimumMargin { get; set; }
    public double? DistributionTolerance { get; set; }
}

/// <summary>Supported category-resolution rollout mode names.</summary>
public static class CategoryResolutionModes
{
    public const string Legacy = "legacy";
    public const string CatalogLlm = "catalog-llm";
    public const string Hybrid = "hybrid";

    public static bool IsRichCatalogMode(string? mode) =>
        string.Equals(mode, CatalogLlm, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Hybrid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Configures periodic MCP category refresh behavior.</summary>
public sealed class McpDiscoveryOptions
{
    public const string SectionName = "McpDiscovery";
    public const int DefaultCategoryRefreshIntervalSeconds = 60;

    public int CategoryRefreshIntervalSeconds { get; set; } =
        DefaultCategoryRefreshIntervalSeconds;
}

/// <summary>Validates category-resolution settings and mode-specific refresh requirements.</summary>
public sealed class CategoryResolutionOptionsValidator
    : IValidateOptions<CategoryResolutionOptions>
{
    /// <summary>
    /// Snapshot age is measured from fetch completion, and a fetch may finish up to one
    /// call timeout after its tick, so the interval leaves that headroom below the expiry.
    /// </summary>
    public static readonly int MaximumRichRefreshIntervalSeconds =
        (int)(CategoryCatalogSnapshotStore.MaximumSnapshotAge
              - McpCategoryEnumRefresher.DefaultCallTimeout).TotalSeconds;

    private readonly McpDiscoveryOptions _discoveryOptions;
    private readonly OpenRouterOptions _openRouterOptions;

    public CategoryResolutionOptionsValidator(
        IOptions<McpDiscoveryOptions> discoveryOptions,
        IOptions<OpenRouterOptions> openRouterOptions)
    {
        _discoveryOptions = discoveryOptions.Value;
        _openRouterOptions = openRouterOptions.Value;
    }

    public ValidateOptionsResult Validate(string? name, CategoryResolutionOptions options)
    {
        var mode = options.Mode?.Trim();
        if (!string.Equals(mode, CategoryResolutionModes.Legacy, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, CategoryResolutionModes.CatalogLlm, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, CategoryResolutionModes.Hybrid, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "CategoryResolution:Mode must be legacy, catalog-llm, or hybrid.");
        }

        if (options.MaxCatalogContextChars <= 0)
        {
            return ValidateOptionsResult.Fail(
                "CategoryResolution:MaxCatalogContextChars must be greater than zero.");
        }

        if (!string.Equals(options.SemanticProvider, CategorySemanticProviders.ChatModel, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.SemanticProvider, CategorySemanticProviders.Jev, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "CategoryResolution:SemanticProvider must be ChatModel or Jev.");
        }

        if (CategoryResolutionModes.IsRichCatalogMode(mode)
            && (_discoveryOptions.CategoryRefreshIntervalSeconds < 1
                || _discoveryOptions.CategoryRefreshIntervalSeconds > MaximumRichRefreshIntervalSeconds))
        {
            return ValidateOptionsResult.Fail(
                $"McpDiscovery:CategoryRefreshIntervalSeconds must be between 1 and {MaximumRichRefreshIntervalSeconds} " +
                "when CategoryResolution:Mode is catalog-llm or hybrid.");
        }

        if (CategoryResolutionModes.IsRichCatalogMode(mode)
            && string.Equals(options.SemanticProvider, CategorySemanticProviders.Jev, StringComparison.OrdinalIgnoreCase))
        {
            var errors = new List<string>();
            var jev = options.Jev;
            if (string.IsNullOrWhiteSpace(jev?.Model))
                errors.Add("CategoryResolution:Jev:Model is required.");
            if (!Uri.TryCreate(jev?.Endpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(endpoint.UserInfo)
                || endpoint.Fragment.Length != 0
                || endpoint.Query.Length != 0
                || endpoint.AbsolutePath != "/api/alpha/decisions")
                errors.Add("CategoryResolution:Jev:Endpoint must be an absolute HTTPS Decisions URL without credentials, query or fragment.");
            if (jev?.TimeoutSeconds is null or < 1 or > 300)
                errors.Add("CategoryResolution:Jev:TimeoutSeconds must be between 1 and 300.");
            if (string.IsNullOrWhiteSpace(_openRouterOptions.ApiKey))
                errors.Add("OpenRouter:ApiKey is required when Jev is enabled.");

            var policy = jev?.Policy;
            if (string.IsNullOrWhiteSpace(policy?.Version))
                errors.Add("CategoryResolution:Jev:Policy:Version is required.");
            if (string.IsNullOrWhiteSpace(policy?.EvaluatedModel)
                || !string.Equals(policy.EvaluatedModel, jev?.Model, StringComparison.Ordinal))
                errors.Add("CategoryResolution:Jev:Policy:EvaluatedModel must match Jev:Model.");
            if (policy?.EvaluatedCatalogVersion is null
                || !System.Text.RegularExpressions.Regex.IsMatch(
                    policy.EvaluatedCatalogVersion, "^sha256:[0-9a-f]{64}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                errors.Add("CategoryResolution:Jev:Policy:EvaluatedCatalogVersion must be a catalog digest.");
            ValidateFraction(policy?.MinimumConfidence, "MinimumConfidence", errors);
            ValidateFraction(policy?.MinimumProbability, "MinimumProbability", errors);
            ValidateFraction(policy?.MinimumMargin, "MinimumMargin", errors);
            if (policy?.DistributionTolerance is not double tolerance
                || !double.IsFinite(tolerance) || tolerance <= 0 || tolerance > 0.01)
                errors.Add("CategoryResolution:Jev:Policy:DistributionTolerance must be greater than 0 and at most 0.01.");
            if (errors.Count > 0) return ValidateOptionsResult.Fail(errors);
        }

        return ValidateOptionsResult.Success;
    }

    private static void ValidateFraction(double? value, string name, ICollection<string> errors)
    {
        if (value is not double number || !double.IsFinite(number) || number < 0 || number > 1)
            errors.Add($"CategoryResolution:Jev:Policy:{name} must be between 0 and 1.");
    }
}
