using System.Collections.Immutable;

namespace ai_assistant_service.Services.Mcp;

public sealed record CategoryCatalogItem(
    string Id,
    string DisplayName,
    string Description,
    ImmutableArray<string> Aliases);

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

public sealed record CategoryCatalogReadiness(
    string Status,
    bool Required,
    bool Usable,
    string? CatalogVersion,
    DateTimeOffset? LastSuccessfulRefreshAt,
    string? Detail);
