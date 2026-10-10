using ai_assistant_service.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace ai_assistant_service.Tests;

public class AiChatOptionsValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(20)]
    public void Validator_AcceptsSupportedHistoryLimits(int limit)
    {
        var result = new AiChatOptionsValidator().Validate(
            Options.DefaultName,
            new AiChatOptions { HistoryMessageLimit = limit });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(21)]
    public void Validator_RejectsUnsupportedHistoryLimits(int limit)
    {
        var result = new AiChatOptionsValidator().Validate(
            Options.DefaultName,
            new AiChatOptions { HistoryMessageLimit = limit });

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "AiChat:HistoryMessageLimit",
                StringComparison.Ordinal));
    }
}
