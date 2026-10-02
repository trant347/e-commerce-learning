using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ai_assistant_service.Services;

/// <summary>
/// Removes numeric search filters the model invented. Small models sometimes add
/// e.g. <c>minRating=4.5</c> to a request that never mentioned ratings, which
/// silently excludes matching providers. A rate or rating filter is kept only when
/// its value appears as a number in the user's own messages.
/// </summary>
public static class SearchFilterGrounding
{
    public const string SearchToolName = "search_task_masters";

    private static readonly string[] NumericFilters = ["minRate", "maxRate", "minRating"];

    private static readonly Regex NumberPattern = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex ThousandsSeparator = new(@"(?<=\d),(?=\d{3}\b)", RegexOptions.Compiled);

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

        var userNumbers = ExtractNumbers(userMessages);
        var removed = new List<string>();
        var kept = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in arguments)
        {
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
