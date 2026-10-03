using ai_assistant_service.Services.Mcp;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Configuration;

/// <summary>Configures category-resolution rollout mode and rich-catalog limits.</summary>
public sealed class CategoryResolutionOptions
{
    public const string SectionName = "CategoryResolution";
    public const int DefaultMaxCatalogContextChars = 12_000;

    public string Mode { get; set; } = CategoryResolutionModes.Legacy;
    public int MaxCatalogContextChars { get; set; } = DefaultMaxCatalogContextChars;
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

    public CategoryResolutionOptionsValidator(IOptions<McpDiscoveryOptions> discoveryOptions)
    {
        _discoveryOptions = discoveryOptions.Value;
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

        if (CategoryResolutionModes.IsRichCatalogMode(mode)
            && (_discoveryOptions.CategoryRefreshIntervalSeconds < 1
                || _discoveryOptions.CategoryRefreshIntervalSeconds > MaximumRichRefreshIntervalSeconds))
        {
            return ValidateOptionsResult.Fail(
                $"McpDiscovery:CategoryRefreshIntervalSeconds must be between 1 and {MaximumRichRefreshIntervalSeconds} " +
                "when CategoryResolution:Mode is catalog-llm or hybrid.");
        }

        return ValidateOptionsResult.Success;
    }
}
