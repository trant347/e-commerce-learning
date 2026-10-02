using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ai_assistant_service.Services;

/// <summary>
/// Removes search filters the model invented. Numeric filters must match a number
/// in the user's own messages. A category is also removed from a location-only
/// search when the conversation names no service.
/// </summary>
public static class SearchFilterGrounding
{
    public const string SearchToolName = "search_task_masters";

    private static readonly string[] NumericFilters = ["minRate", "maxRate", "minRating"];
    private static readonly HashSet<string> LocationOnlyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "all", "an", "any", "anyone", "are", "around", "at", "available",
        "affordable", "availability", "based", "best", "budget", "can", "check",
        "cheapest", "closest", "could", "do", "dollar", "dollars", "expensive",
        "find", "for", "from", "get", "good", "have", "highest", "hour", "hourly",
        "i", "in", "is", "less", "list", "located", "looking", "lowest", "master",
        "masters", "max", "maximum", "me", "min", "minimum", "more", "most", "near",
        "nearby", "of", "over", "people", "person", "please", "professional",
        "provider", "providers", "rate", "rated", "rates", "rating", "service",
        "services", "show", "someone", "specific", "stars", "task", "taskmaster",
        "taskmasters", "than", "the", "there", "to", "top", "under", "usd", "want",
        "what", "who", "without", "you"
    };
    private static readonly HashSet<string> UsStateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA",
        "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
        "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
        "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
        "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "DC"
    };

    private static readonly Regex NumberPattern = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex ThousandsSeparator = new(@"(?<=\d),(?=\d{3}\b)", RegexOptions.Compiled);
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, JsonElement> RemoveUngroundedFilters(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IEnumerable<string?> userMessages,
        out IReadOnlyList<string> removedFilters)
    {
        removedFilters = Array.Empty<string>();
        if (!string.Equals(toolName, SearchToolName, StringComparison.OrdinalIgnoreCase))
        {
            return arguments;
        }

        var messages = userMessages.Where(message => !string.IsNullOrWhiteSpace(message)).ToArray();
        var userNumbers = ExtractNumbers(messages);
        var removeCategory = IsLocationOnlySearch(arguments, messages);
        var removed = new List<string>();
        var kept = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in arguments)
        {
            if (removeCategory && string.Equals(name, "category", StringComparison.OrdinalIgnoreCase))
            {
                removed.Add(name);
                continue;
            }

            var isNumericFilter = NumericFilters.Contains(name, StringComparer.OrdinalIgnoreCase);
            if (isNumericFilter
                && TryReadNumber(value, out var number)
                && !userNumbers.Any(n => Math.Abs(n - number) < 1e-9))
            {
                removed.Add(name);
                continue;
            }
            kept[name] = value;
        }

        if (removed.Count == 0)
        {
            return arguments;
        }

        removedFilters = removed;
        return kept;
    }

    private static bool IsLocationOnlySearch(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyList<string?> userMessages)
    {
        if (!TryReadArgument(arguments, "category", out var category)
            || string.IsNullOrWhiteSpace(category)
            || !TryReadArgument(arguments, "location", out var location)
            || string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        var locationWords = ExtractWords(location).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return userMessages
            .SelectMany(ExtractWords)
            .All(word => locationWords.Contains(word)
                || UsStateCodes.Contains(word)
                || word.All(char.IsAsciiDigit)
                || LocationOnlyWords.Contains(word));
    }

    private static IEnumerable<string> ExtractWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in WordPattern.Matches(text))
        {
            yield return match.Value;
        }
    }

    private static bool TryReadArgument(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string name,
        out string? value)
    {
        var argument = arguments.FirstOrDefault(pair =>
            string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        if (argument.Key is null)
        {
            value = null;
            return false;
        }

        value = argument.Value.ValueKind switch
        {
            JsonValueKind.String => argument.Value.GetString(),
            JsonValueKind.Array when argument.Value.GetArrayLength() == 1
                && argument.Value[0].ValueKind == JsonValueKind.String
                => argument.Value[0].GetString(),
            _ => null
        };
        return true;
    }

    private static List<double> ExtractNumbers(IEnumerable<string?> texts)
    {
        var numbers = new List<double>();
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var normalized = ThousandsSeparator.Replace(text, string.Empty);
            foreach (Match match in NumberPattern.Matches(normalized))
            {
                if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    numbers.Add(n);
                }
            }
        }
        return numbers;
    }

    /// <summary>
    /// Reads the model's value the same way product-service will (digits and dots only).
    /// Blank or unparsable values return false and are left for the tool to ignore.
    /// </summary>
    private static bool TryReadNumber(JsonElement value, out double number)
    {
        number = 0;
        var raw = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array when value.GetArrayLength() == 1 => value[0].ValueKind switch
            {
                JsonValueKind.Number => value[0].GetRawText(),
                JsonValueKind.String => value[0].GetString(),
                _ => null
            },
            _ => null
        };
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var cleaned = new string(raw.Where(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        return cleaned.Length > 0
            && double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
