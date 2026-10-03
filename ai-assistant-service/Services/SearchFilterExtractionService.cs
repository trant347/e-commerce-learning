using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services;

/// <summary>Validated hard search constraints and soft preferences from user-authored context.</summary>
public sealed record SearchSoftPreference(
    string Field,
    JsonElement Value,
    string Unit,
    string Evidence);

/// <summary>Validated hard search constraints and soft preferences from user-authored context.</summary>
public sealed record SearchFilterEnvelope(
    IReadOnlyDictionary<string, JsonElement> HardArguments,
    IReadOnlyList<SearchSoftPreference> SoftPreferences,
    bool NeedsClarification);

/// <summary>Extracts explicit supported filters without turning preferences into exclusions.</summary>
public sealed class SearchFilterExtractionService
{
    private static readonly HashSet<string> Supported = new(
        ["location", "minRate", "maxRate", "minRating"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> StateCodes = new(
        "AL AK AZ AR CA CO CT DE FL GA HI ID IL IN IA KS KY LA ME MD MA MI MN MS MO MT NC ND NE NH NJ NM NV NY OH OK OR PA RI SC SD TN TX UT VA VT WA WV WI WY DC".Split(' '),
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> StateNames = new(
        ("alabama|alaska|arizona|arkansas|california|colorado|connecticut|delaware|florida|georgia|hawaii|idaho|illinois|indiana|iowa|kansas|kentucky|louisiana|maine|maryland|massachusetts|michigan|minnesota|mississippi|missouri|montana|nebraska|new hampshire|new jersey|new mexico|new york|north carolina|north dakota|ohio|oklahoma|oregon|pennsylvania|rhode island|south carolina|south dakota|tennessee|texas|utah|vermont|virginia|washington|west virginia|wisconsin|wyoming|district of columbia").Split('|'),
        StringComparer.OrdinalIgnoreCase);
    private static readonly Regex LocationPattern = new(
        @"^[\p{L} .'-]{1,80}(?:,\s*[\p{L} ]{2,30})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HourlyPattern = new(
        @"(?:/\s*(?:h|hr|hour)\b|\bper\s+hour\b|\bhourly\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UsdPattern = new(
        @"(?:\$|\bUSD\b|\bdollars?\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MaxRatePattern = new(
        @"(?:\b(?:under|below|budget|less than|at most|no more than|up to|max(?:imum)?)\b|<=)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MinRatePattern = new(
        @"(?:\b(?:over|above|more than|at least|min(?:imum)?)\b|>=)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SoftPattern = new(
        @"\b(?:prefer(?:ably)?|ideally|if possible|nice to have|roughly|around)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex UnsupportedDateRequirement = new(
        @"\b(?:must|need|only)\b.{0,45}\b(?:available|tomorrow|today|next week)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TotalBudgetRequirement = new(
        @"\b(?:under|below|less than|at most|budget|max(?:imum)?|no more than)\b.{0,25}(?:\$|USD|dollars?)\s*\d+(?:\.\d+)?\s*(?:total|for (?:the )?(?:whole )?job)\b|\b(?:total|fixed)\s+budget\b.{0,25}(?:\$|USD|dollars?)\s*\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HourlyRateRequirement = new(
        @"\b(?<direction>under|below|less than|at most|no more than|up to|budget|max(?:imum)?|over|above|more than|at least|min(?:imum)?)\s+(?:\$|USD\s*|dollars?\s*)\s*(?<amount>\d+(?:\.\d+)?)\s*(?:/\s*(?:h|hr|hour)\b|per\s+hour\b|hourly\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RatingRequirement = new(
        @"\b(?:rating\s+(?:of\s+)?(?:at least|above|over)|(?:at least|above|over|minimum)\s*)\s*(?<amount>\d+(?:\.\d+)?)\s*(?:stars?|rating)?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CityStateRequirement = new(
        @"\bin\s+(?<place>[\p{L} .'-]{2,50},\s*[A-Z]{2})(?=[^\p{L}]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<SearchFilterEnvelope> ExtractAsync(
        string request,
        IReadOnlyList<string> userHistory,
        ITaskChatModelClient client,
        CancellationToken cancellationToken)
    {
        var messages = new[]
        {
            new ChatModelMessage(ChatModelRoles.System,
                "Extract only explicitly user-stated search filters. Return one JSON object with hard (object), soft (array), " +
                "and unsupportedHard (array). Hard fields can only be location, minRate, maxRate or minRating. " +
                "Each hard field is {value,evidence,unit}; location uses unit 'place', rates 'usd_per_hour', rating 'rating_0_5'. " +
                "Evidence must be an exact substring of a user message. Numeric rates require explicit hourly USD units; " +
                "total-job budgets must go in unsupportedHard if mandatory. Soft preferences must never become hard filters. " +
                "Each soft entry is {field,value,unit,evidence}; each unsupportedHard entry is {evidence}. " +
                "Do not assume availability or date support. Unspecified filters stay absent. No prose or code fences."),
            new ChatModelMessage(ChatModelRoles.User, JsonSerializer.Serialize(new
            {
                currentRequest = request,
                clarificationHistory = userHistory
            }))
        };
        var response = await client.ChatAsync(messages, tools: null, cancellationToken);
        if (response.Message.ToolCalls is { Count: > 0 }
            || !TryParse(response.Message.Content, request, userHistory, out var envelope))
            return new SearchFilterEnvelope(new Dictionary<string, JsonElement>(), [], true);
        return envelope!;
    }

    internal static bool TryParse(
        string? text,
        string request,
        IReadOnlyList<string> userHistory,
        out SearchFilterEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !ExactKeys(root, ["hard", "soft", "unsupportedHard"])
                || root.GetProperty("hard").ValueKind != JsonValueKind.Object
                || root.GetProperty("soft").ValueKind != JsonValueKind.Array
                || root.GetProperty("unsupportedHard").ValueKind != JsonValueKind.Array)
                return false;

            var userTexts = userHistory.Append(request).ToArray();
            var hard = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.GetProperty("hard").EnumerateObject())
            {
                if (!Supported.Contains(property.Name)
                    || !TryHard(property.Name, property.Value, userTexts, out var value)
                    || !hard.TryAdd(property.Name, value))
                    return false;
            }
            if (hard.TryGetValue("minRate", out var min)
                && hard.TryGetValue("maxRate", out var max)
                && min.GetDouble() > max.GetDouble())
                return false;

            var soft = new List<SearchSoftPreference>();
            foreach (var item in root.GetProperty("soft").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !ExactKeys(item, ["field", "value", "unit", "evidence"])
                    || !TryText(item, "field", out var field) || !Supported.Contains(field)
                    || !TryText(item, "evidence", out var evidence)
                    || !TryText(item, "unit", out var unit)
                    || !TryFilterValue(field, item, evidence, unit, out var value)
                    || !userTexts.Any(text => text.Contains(evidence, StringComparison.OrdinalIgnoreCase)))
                    return false;
                soft.Add(new SearchSoftPreference(field, value, unit, evidence));
            }

            var unsupported = root.GetProperty("unsupportedHard");
            foreach (var item in unsupported.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !ExactKeys(item, ["evidence"])
                    || !TryText(item, "evidence", out var evidence)
                    || !userTexts.Any(text => text.Contains(evidence, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            envelope = new SearchFilterEnvelope(
                hard, soft,
                unsupported.GetArrayLength() > 0
                || userTexts.Any(text => UnsupportedDateRequirement.IsMatch(text)
                    || TotalBudgetRequirement.Matches(text)
                        .Any(match => IsHardOccurrence(text, match.Index, match.Length)))
                || !AllExplicitHardFiltersPreserved(userTexts, hard));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryHard(
        string name,
        JsonElement item,
        IReadOnlyList<string> userTexts,
        out JsonElement value)
    {
        value = default;
        if (item.ValueKind != JsonValueKind.Object
            || !ExactKeys(item, ["value", "evidence", "unit"])
            || !TryText(item, "evidence", out var evidence)
            || !TryText(item, "unit", out var unit)
            || !userTexts.Any(text => ContainsHardEvidence(text, evidence)))
            return false;

        if (name == "maxRate" && !MaxRatePattern.IsMatch(evidence)
            || name == "minRate" && !MinRatePattern.IsMatch(evidence))
            return false;
        return TryFilterValue(name, item, evidence, unit, out value);
    }

    private static bool TryFilterValue(
        string name, JsonElement item, string evidence, string unit, out JsonElement value)
    {
        value = default;
        var raw = item.GetProperty("value");
        if (name == "location")
        {
            if (unit != "place" || raw.ValueKind != JsonValueKind.String
                || !TryText(item, "value", out var place)
                || !evidence.Contains(place, StringComparison.OrdinalIgnoreCase)
                || !ValidLocation(place))
                return false;
            value = JsonSerializer.SerializeToElement(place);
            return true;
        }

        if (raw.ValueKind != JsonValueKind.Number
            || !raw.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number < 0
            || number > (name == "minRating" ? 5 : 100_000)
            || (name == "minRating"
                ? unit != "rating_0_5" || !Regex.IsMatch(
                    evidence, @"\b(?:rating|rated|stars?)\b", RegexOptions.IgnoreCase)
                : unit != "usd_per_hour" || !HourlyPattern.IsMatch(evidence)
                    || !UsdPattern.IsMatch(evidence))
            || !Regex.IsMatch(
                evidence, $@"(?<!\d){Regex.Escape(number.ToString(CultureInfo.InvariantCulture))}(?!\d)",
                RegexOptions.CultureInvariant))
            return false;
        value = JsonSerializer.SerializeToElement(number);
        return true;
    }

    private static bool ContainsHardEvidence(string text, string evidence)
    {
        var start = 0;
        while ((start = text.IndexOf(evidence, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (IsHardOccurrence(text, start, evidence.Length)) return true;
            start += evidence.Length;
            if (start >= text.Length) break;
        }
        return false;
    }

    private static bool IsHardOccurrence(string text, int start, int length)
    {
        var from = Math.Max(0, start - 60);
        var before = text[from..start];
        var punctuation = before.LastIndexOfAny([',', ';', '.', '!', '?']);
        var conjunction = Math.Max(
            before.LastIndexOf(" but ", StringComparison.OrdinalIgnoreCase),
            before.LastIndexOf(" and ", StringComparison.OrdinalIgnoreCase));
        from += Math.Max(punctuation + 1, conjunction < 0 ? 0 : conjunction + 5);
        return !SoftPattern.IsMatch(text[from..(start + length)]);
    }

    private static bool AllExplicitHardFiltersPreserved(
        IEnumerable<string> userTexts,
        IReadOnlyDictionary<string, JsonElement> hard)
    {
        foreach (var text in userTexts)
        {
            foreach (Match match in HourlyRateRequirement.Matches(text))
            {
                if (!IsHardOccurrence(text, match.Index, match.Length)) continue;
                var direction = match.Groups["direction"].Value;
                var name = MinRatePattern.IsMatch(direction) ? "minRate" : "maxRate";
                if (!double.TryParse(
                        match.Groups["amount"].Value,
                        NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                        out var amount)
                    || !hard.TryGetValue(name, out var supplied)
                    || supplied.GetDouble() != amount)
                    return false;
            }
            foreach (Match match in RatingRequirement.Matches(text))
            {
                if (!IsHardOccurrence(text, match.Index, match.Length)) continue;
                if (!double.TryParse(
                        match.Groups["amount"].Value,
                        NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                        out var amount)
                    || !hard.TryGetValue("minRating", out var supplied)
                    || supplied.GetDouble() != amount)
                    return false;
            }
            foreach (Match match in CityStateRequirement.Matches(text))
            {
                if (!IsHardOccurrence(text, match.Index, match.Length)) continue;
                if (!hard.TryGetValue("location", out var supplied)
                    || !string.Equals(
                        supplied.GetString(), match.Groups["place"].Value,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        return true;
    }

    private static bool ValidLocation(string place)
    {
        if (!LocationPattern.IsMatch(place)) return false;
        var parts = place.Split(',');
        if (parts.Length == 1)
            return place.Length != 2 || StateCodes.Contains(place);
        var state = parts[1].Trim();
        return StateCodes.Contains(state) || StateNames.Contains(state);
    }

    private static bool ExactKeys(JsonElement element, string[] keys)
    {
        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == keys.Length
            && properties.Distinct(StringComparer.Ordinal).Count() == keys.Length
            && keys.All(key => properties.Contains(key, StringComparer.Ordinal));
    }

    private static bool TryText(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = field.GetString() ?? string.Empty)
            && value == value.Trim();
    }
}
