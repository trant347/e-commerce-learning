using System.Collections.Immutable;

namespace ai_assistant_service.Services.Mcp;

/// <summary>Immutable public metadata for one product-owned category.</summary>
public sealed record CategoryCatalogItem(
    string Id,
    string DisplayName,
    string Description,
    ImmutableArray<string> Aliases);

/// <summary>A validated, request-capturable rich category catalog snapshot.</summary>
public sealed record CategoryCatalogSnapshot(
    int SchemaVersion,
    string CatalogVersion,
    ImmutableArray<CategoryCatalogItem> Categories,
    string DecisionContext,
    DateTimeOffset LastSuccessfulRefreshAt)
{
    public ImmutableArray<string> CategoryIds { get; } =
        Categories.Select(category => category.Id).ToImmutableArray();
}

/// <summary>Reports whether the rich catalog is required and usable for new decisions.</summary>
public sealed record CategoryCatalogReadiness(
    string Status,
    bool Required,
    bool Usable,
    string? CatalogVersion,
    DateTimeOffset? LastSuccessfulRefreshAt,
    string? Detail);
