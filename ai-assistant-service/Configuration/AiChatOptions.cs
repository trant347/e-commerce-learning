using Microsoft.Extensions.Options;

namespace ai_assistant_service.Configuration;

public sealed class AiChatOptions
{
    public const string SectionName = "AiChat";
    public const int DefaultHistoryMessageLimit = 5;
    public const int MaximumHistoryMessageLimit = 20;

    public int HistoryMessageLimit { get; set; } = DefaultHistoryMessageLimit;
}

public sealed class AiChatOptionsValidator : IValidateOptions<AiChatOptions>
{
    public ValidateOptionsResult Validate(string? name, AiChatOptions options) =>
        options.HistoryMessageLimit is >= 0 and <= AiChatOptions.MaximumHistoryMessageLimit
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"AiChat:HistoryMessageLimit must be between 0 and {AiChatOptions.MaximumHistoryMessageLimit}.");
}
