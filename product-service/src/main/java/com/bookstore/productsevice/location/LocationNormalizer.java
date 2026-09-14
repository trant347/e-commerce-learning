package com.bookstore.productsevice.location;

import org.springframework.stereotype.Component;

import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.regex.Pattern;

import static java.util.Map.entry;

@Component
public class LocationNormalizer {

    private static final Pattern CITY_PATTERN = Pattern.compile("[\\p{L} .'-]+");

    private static final Map<String, String> STATE_CODES = Map.ofEntries(
            entry("alabama", "AL"), entry("alaska", "AK"), entry("arizona", "AZ"),
            entry("arkansas", "AR"), entry("california", "CA"), entry("colorado", "CO"),
            entry("connecticut", "CT"), entry("delaware", "DE"), entry("florida", "FL"),
            entry("georgia", "GA"), entry("hawaii", "HI"), entry("idaho", "ID"),
            entry("illinois", "IL"), entry("indiana", "IN"), entry("iowa", "IA"),
            entry("kansas", "KS"), entry("kentucky", "KY"), entry("louisiana", "LA"),
            entry("maine", "ME"), entry("maryland", "MD"), entry("massachusetts", "MA"),
            entry("michigan", "MI"), entry("minnesota", "MN"), entry("mississippi", "MS"),
            entry("missouri", "MO"), entry("montana", "MT"), entry("nebraska", "NE"),
            entry("nevada", "NV"), entry("new hampshire", "NH"), entry("new jersey", "NJ"),
            entry("new mexico", "NM"), entry("new york", "NY"), entry("north carolina", "NC"),
            entry("north dakota", "ND"), entry("ohio", "OH"), entry("oklahoma", "OK"),
            entry("oregon", "OR"), entry("pennsylvania", "PA"), entry("rhode island", "RI"),
            entry("south carolina", "SC"), entry("south dakota", "SD"), entry("tennessee", "TN"),
            entry("texas", "TX"), entry("utah", "UT"), entry("vermont", "VT"),
            entry("virginia", "VA"), entry("washington", "WA"), entry("west virginia", "WV"),
            entry("wisconsin", "WI"), entry("wyoming", "WY"), entry("district of columbia", "DC"));

    private static final Set<String> VALID_STATE_CODES = Set.copyOf(STATE_CODES.values());

    public NormalizedLocation normalizeForWrite(String value) {
        String input = requireInput(value);
        String[] parts = input.split(",", -1);
        if (parts.length != 2) {
            throw new LocationValidationException(
                    "Location must use the format 'City, ST', for example 'Chicago, IL'.");
        }

        String city = normalizeCity(parts[0]);
        String stateCode = normalizeState(parts[1]);
        return new NormalizedLocation(toDisplayCity(city) + ", " + stateCode, city, stateCode);
    }

    public LocationSearchCriteria normalizeForSearch(String value) {
        String input = requireInput(value);
        if (input.contains(",")) {
            NormalizedLocation location = normalizeForWrite(input);
            return new LocationSearchCriteria(
                    LocationSearchCriteria.MatchMode.CITY_AND_STATE,
                    location.city(),
                    location.stateCode());
        }

        String normalizedInput = normalizeWhitespace(input);
        String upper = normalizedInput.toUpperCase(Locale.US);
        if (upper.length() == 2 && VALID_STATE_CODES.contains(upper)) {
            return new LocationSearchCriteria(
                    LocationSearchCriteria.MatchMode.STATE,
                    null,
                    upper);
        }

        String stateCode = STATE_CODES.get(normalizedInput.toLowerCase(Locale.US));
        if (stateCode != null) {
            return new LocationSearchCriteria(
                    LocationSearchCriteria.MatchMode.CITY_OR_STATE,
                    normalizeCity(normalizedInput),
                    stateCode);
        }

        return new LocationSearchCriteria(
                LocationSearchCriteria.MatchMode.CITY,
                normalizeCity(normalizedInput),
                null);
    }

    private static String requireInput(String value) {
        if (value == null || value.isBlank()) {
            throw new LocationValidationException("Location is required.");
        }
        return normalizeWhitespace(value);
    }

    private static String normalizeCity(String value) {
        String city = normalizeWhitespace(value).toLowerCase(Locale.US);
        if (city.isBlank() || !CITY_PATTERN.matcher(city).matches()) {
            throw new LocationValidationException("Location contains an invalid city.");
        }
        return city;
    }

    private static String normalizeState(String value) {
        String state = normalizeWhitespace(value);
        String upper = state.toUpperCase(Locale.US);
        if (upper.length() == 2 && VALID_STATE_CODES.contains(upper)) {
            return upper;
        }

        String stateCode = STATE_CODES.get(state.toLowerCase(Locale.US));
        if (stateCode == null) {
            throw new LocationValidationException(
                    "Location must contain a valid US state name or two-letter state code.");
        }
        return stateCode;
    }

    private static String normalizeWhitespace(String value) {
        return value == null ? "" : value.trim().replaceAll("\\s+", " ");
    }

    private static String toDisplayCity(String city) {
        StringBuilder result = new StringBuilder(city.length());
        boolean capitalize = true;
        for (char character : city.toCharArray()) {
            if (capitalize && Character.isLetter(character)) {
                result.append(Character.toUpperCase(character));
                capitalize = false;
            } else {
                result.append(character);
                capitalize = !Character.isLetter(character);
            }
        }
        return result.toString();
    }
}
