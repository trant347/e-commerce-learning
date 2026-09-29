using ai_assistant_service.Services;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Configuration;

public sealed class PromptOptions
{
    public const string SectionName = "PromptOptions";

    public string SystemPrompt { get; set; } = "You are a TaskMaster marketplace assistant.";
    public List<FewShotExample> FewShotExamples { get; set; } = [];
}

public sealed class PromptOptionsValidator : IValidateOptions<PromptOptions>
{
    public ValidateOptionsResult Validate(string? name, PromptOptions options)
    {
        try
        {
            AssistantPrompt.CreateMessages(options);
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
