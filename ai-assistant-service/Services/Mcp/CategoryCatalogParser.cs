using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ai_assistant_service.Services.Mcp;

/// <summary>Strictly validates and materializes product-service rich catalog responses.</summary>
internal static partial class CategoryCatalogParser
{
    internal const int SupportedSchemaVersion = 1;
    internal const int MaximumCategories = 100;
    private const int MaximumIdLength = 64;
    private const int MaximumDisplayNameLength = 80;
    private const int MaximumDescriptionLength = 500;
    private const int MaximumAliases = 20;
    private const int MaximumAliasLength = 80;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static bool TryParse(
        string raw,
        int maximumContextChars,
        DateTimeOffset refreshedAt,
        out CategoryCatalogSnapshot? snapshot,
        out string error)
    {
        snapshot = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "get_category_catalog returned an empty response.";
            return false;
        }

        try
        {
            using var outerDocument = JsonDocument.Parse(raw);
            var root = outerDocument.RootElement;
            JsonDocument? innerDocument = null;
            try
            {
                if (root.ValueKind == JsonValueKind.String)
                {
                    var inner = root.GetString();
                    if (string.IsNullOrWhiteSpace(inner))
                    {
                        error = "get_category_catalog returned an empty wrapped response.";
                        return false;
                    }

                    innerDocument = JsonDocument.Parse(inner);
                    root = innerDocument.RootElement;
                }

                return TryParseRoot(
                    root,
                    maximumContextChars,
                    refreshedAt,
                    out snapshot,
                    out error);
            }
            finally
            {
                innerDocument?.Dispose();
            }
        }
        catch (JsonException)
        {
            error = "get_category_catalog returned malformed JSON.";
            return false;
        }
    }

    private static bool TryParseRoot(
        JsonElement root,
        int maximumContextChars,
        DateTimeOffset refreshedAt,
        out CategoryCatalogSnapshot? snapshot,
        out string error)
    {
        snapshot = null;
        error = string.Empty;

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "The category catalog must be a JSON object.";
            return false;
        }

        if (root.TryGetProperty("error", out _))
        {
            error = "The category catalog tool returned an error.";
            return false;
        }

        if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement)
            || !schemaVersionElement.TryGetInt32(out var schemaVersion)
            || schemaVersion != SupportedSchemaVersion)
        {
            error = $"The category catalog schemaVersion must be {SupportedSchemaVersion}.";
            return false;
        }

        if (!TryGetRequiredString(root, "catalogVersion", out var catalogVersion)
            || !CatalogVersionPattern().IsMatch(catalogVersion))
        {
            error = "The category catalog catalogVersion is invalid.";
            return false;
        }

        if (!root.TryGetProperty("categories", out var categoriesElement)
            || categoriesElement.ValueKind != JsonValueKind.Array)
        {
            error = "The category catalog categories field must be an array.";
            return false;
        }

        var categoryCount = categoriesElement.GetArrayLength();
        if (categoryCount == 0)
        {
            error = "The category catalog contains no categories.";
            return false;
        }
        if (categoryCount > MaximumCategories)
        {
            error = $"The category catalog exceeds the {MaximumCategories}-category limit.";
            return false;
        }

        var categories = ImmutableArray.CreateBuilder<CategoryCatalogItem>(categoryCount);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var categoryElement in categoriesElement.EnumerateArray())
        {
            if (!TryParseCategory(categoryElement, ids, out var category, out error))
            {
                return false;
            }
            categories.Add(category!);
        }

        var contextPayload = new CatalogContextPayload(
            schemaVersion,
            catalogVersion,
            categories.ToImmutable());
        var decisionContext = JsonSerializer.Serialize(contextPayload, SerializerOptions);
        if (decisionContext.Length > maximumContextChars)
        {
            error =
                $"The category catalog context exceeds the {maximumContextChars}-character limit.";
            return false;
        }

        snapshot = new CategoryCatalogSnapshot(
            schemaVersion,
            catalogVersion,
            contextPayload.Categories,
            decisionContext,
            refreshedAt);
        return true;
    }

    private static bool TryParseCategory(
        JsonElement element,
        ISet<string> ids,
        out CategoryCatalogItem? category,
        out string error)
    {
        category = null;
        error = string.Empty;

        if (element.ValueKind != JsonValueKind.Object
            || !TryGetRequiredString(element, "id", out var id)
            || id.Length is < 2 or > MaximumIdLength
            || !CategoryIdPattern().IsMatch(id))
        {
            error = "A category has an invalid id.";
            return false;
        }
        if (!ids.Add(id))
        {
            error = $"The category catalog contains duplicate id '{id}'.";
            return false;
        }

        if (!TryGetRequiredString(element, "displayName", out var displayName)
            || displayName.Length is < 2 or > MaximumDisplayNameLength)
        {
            error = $"Category '{id}' has an invalid displayName.";
            return false;
        }
        if (!TryGetRequiredString(element, "description", out var description)
            || description.Length > MaximumDescriptionLength)
        {
            error = $"Category '{id}' has an invalid description.";
            return false;
        }
        if (!element.TryGetProperty("aliases", out var aliasesElement)
            || aliasesElement.ValueKind != JsonValueKind.Array
            || aliasesElement.GetArrayLength() > MaximumAliases)
        {
            error = $"Category '{id}' has an invalid aliases field.";
            return false;
        }

        var aliases = ImmutableArray.CreateBuilder<string>();
        var uniqueAliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var aliasElement in aliasesElement.EnumerateArray())
        {
            if (aliasElement.ValueKind != JsonValueKind.String)
            {
                error = $"Category '{id}' contains a non-string alias.";
                return false;
            }

            var alias = aliasElement.GetString();
            if (string.IsNullOrWhiteSpace(alias)
                || alias.Length is < 2 or > MaximumAliasLength
                || alias != alias.Trim()
                || !uniqueAliases.Add(alias))
            {
                error = $"Category '{id}' contains an invalid or duplicate alias.";
                return false;
            }
            aliases.Add(alias);
        }

        category = new CategoryCatalogItem(
            id,
            displayName,
            description,
            aliases.ToImmutable());
        return true;
    }

    private static bool TryGetRequiredString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value) && value == value.Trim();
    }

    private sealed record CatalogContextPayload(
        int SchemaVersion,
        string CatalogVersion,
        ImmutableArray<CategoryCatalogItem> Categories);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryIdPattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogVersionPattern();
}
