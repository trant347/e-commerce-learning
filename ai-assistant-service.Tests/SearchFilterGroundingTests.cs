using System.Text.Json;
using ai_assistant_service.Services;
using Xunit;

namespace ai_assistant_service.Tests;

public class SearchFilterGroundingTests
{
    private static Dictionary<string, JsonElement> Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void RemovesRatingTheUserNeverMentioned()
    {
        var result = SearchFilterGrounding.RemoveUngroundedFilters(
            "search_task_masters",
            Args("{\"category\":\"tutoring\",\"location\":\"San Francisco\",\"minRating\":\"4.5\"}"),
            new[] { "could you check if any English teacher in San Francisco?" },
            out var removed);

        Assert.Equal(new[] { "minRating" }, removed);
        Assert.False(result.ContainsKey("minRating"));
        Assert.Equal("tutoring", result["category"].GetString());
        Assert.Equal("San Francisco", result["location"].GetString());
    }

    [Theory]
    [InlineData("{\"maxRate\":\"50\"}", "a plumber under $50 an hour")]
    [InlineData("{\"maxRate\":50}", "a plumber under 50 dollars")]
    [InlineData("{\"minRating\":\"4\"}", "rated at least 4 stars")]
    [InlineData("{\"minRate\":\"1000\"}", "more than $1,000")]
    [InlineData("{\"maxRate\":[\"70\"]}", "budget $70/hour")]
    public void KeepsFiltersWhoseValueTheUserStated(string json, string userMessage)
    {
        var args = Args(json);

        var result = SearchFilterGrounding.RemoveUngroundedFilters(
            "search_task_masters", args, new[] { userMessage }, out var removed);

        Assert.Empty(removed);
        Assert.Same(args, result);
    }

    [Fact]
    public void UsesRecentUserHistoryToGroundFilters()
    {
        var result = SearchFilterGrounding.RemoveUngroundedFilters(
            "search_task_masters",
            Args("{\"category\":\"plumbing\",\"maxRate\":\"60\"}"),
            new[] { "what about Chicago?", "I need a plumber, my budget is $60" },
            out var removed);

        Assert.Empty(removed);
        Assert.True(result.ContainsKey("maxRate"));
    }

    [Fact]
    public void LeavesBlankFiltersAndOtherToolsUntouched()
    {
        var blank = Args("{\"category\":\"plumbing\",\"minRating\":\"\"}");
        SearchFilterGrounding.RemoveUngroundedFilters(
            "search_task_masters", blank, new[] { "a plumber" }, out var removedBlank);
        Assert.Empty(removedBlank);

        var other = Args("{\"minRating\":\"4.5\"}");
        var result = SearchFilterGrounding.RemoveUngroundedFilters(
            "get_bookings", other, new[] { "my bookings" }, out var removedOther);
        Assert.Empty(removedOther);
        Assert.Same(other, result);
    }
}
