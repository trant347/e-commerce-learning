using System.Text.Json;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Logging;

namespace ai_assistant_service.Services.Tools;

public sealed class SearchTaskMastersTool : IToolDefinition
{
    private readonly IProductApiClient _client;
    private readonly ILogger<SearchTaskMastersTool> _logger;

    private string[] _categories = [];

    /// <summary>
    /// Called once at startup (from Program.cs) to seed the enum of valid categories
    /// so the LLM normalises user input (e.g. "tutor" → "tutoring").
    /// </summary>
    public void SetCategories(string[] categories) => _categories = categories;

    public string Name => "search_task_masters";

    public string Description =>
        "Search for task masters (service providers) in the marketplace. " +
        "Use this when the user asks about available professionals, skills, pricing, location, or ratings. " +
        "Optionally filter by category (e.g. plumbing, cleaning, tutoring) or location.";

    public object ParametersSchema
    {
        get
        {
            object categorySchema = _categories.Length > 0
                ? (object)new
                {
                    type = "string",
                    description = "Job category to filter by. You MUST use one of the listed enum values — pick the closest match to what the user asked for.",
                    @enum = _categories
                }
                : new
                {
                    type = "string",
                    description = "Job category to filter by, e.g. 'plumbing', 'cleaning', 'tutoring'. Leave empty to return all."
                };

            return new
            {
                type = "object",
                properties = new
                {
                    category = categorySchema,
                    location = new
                    {
                        type = "string",
                        description = "City or region to filter by, e.g. 'New York, NY'. Leave empty to search all locations."
                    }
                },
                required = Array.Empty<string>()
            };
        }
    }

    public SearchTaskMastersTool(IProductApiClient client, ILogger<SearchTaskMastersTool> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken)
    {
        arguments.TryGetValue("category", out var category);
        arguments.TryGetValue("location", out var location);

        _logger.LogDebug("[SearchTaskMastersTool] Raw args: category='{Category}' location='{Location}'",
            category, location);

        // Guard: the model sometimes wraps values in a JSON object, e.g. {"type":"tutoring"}.
        // Extract the first string value from the object so the API receives a plain string.
        category = ExtractStringValue(category);
        location = ExtractStringValue(location);

        _logger.LogInformation("[SearchTaskMastersTool] Resolved args: category='{Category}' location='{Location}'",
            category, location);

        var result = await _client.FetchProductContextAsync(category, location, cancellationToken);

        _logger.LogInformation("[SearchTaskMastersTool] FetchProductContext returned {Length} chars", result?.Length ?? 0);

        return result;
    }

    /// <summary>
    /// Normalises a model-supplied argument value to a plain string.
    /// Handles cases where the model wraps the value in a JSON object or array:
    ///   {"type":"tutoring"}  → "tutoring"
    ///   ["tutoring"]         → "tutoring"
    ///   []                   → null (treat as "no filter")
    ///   "tutoring"           → "tutoring"
    /// </summary>
    private static string? ExtractStringValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('[')) return trimmed;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);

            // JSON object: return first string property value
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        return prop.Value.GetString();
                }
            }

            // JSON array: return first string element, or null for empty array
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                        return element.GetString();
                }
                return null; // empty array → no filter
            }
        }
        catch (JsonException)
        {
            // Not valid JSON — use original value
        }

        return trimmed;
    }
}
