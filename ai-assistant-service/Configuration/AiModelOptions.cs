using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Configuration;

public sealed class AiModelsOptions
{
    public const string SectionName = "AiModels";

    public Dictionary<string, TaskModelOptions> Tasks { get; set; } = new();
}

public sealed class TaskModelOptions
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int? RequestTimeoutSeconds { get; set; }
    public int? MaxOutputTokens { get; set; }
}

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string? BaseUrl { get; set; }
}

public static class ChatModelProviders
{
    public const string Ollama = "Ollama";
    public const string OpenRouter = "OpenRouter";

    public static bool TryNormalize(string? provider, out string normalized)
    {
        if (string.Equals(provider, Ollama, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Ollama;
            return true;
        }

        if (string.Equals(provider, OpenRouter, StringComparison.OrdinalIgnoreCase))
        {
            normalized = OpenRouter;
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}

public sealed partial class AiModelsOptionsValidator : IValidateOptions<AiModelsOptions>
{
    private readonly OllamaOptions _ollamaOptions;

    public AiModelsOptionsValidator(IOptions<OllamaOptions> ollamaOptions)
    {
        _ollamaOptions = ollamaOptions.Value;
    }

    public ValidateOptionsResult Validate(string? name, AiModelsOptions options)
    {
        var errors = new List<string>();

        if (options.Tasks.Count == 0)
        {
            return ValidateOptionsResult.Fail(
                "AiModels:Tasks must contain a complete 'chat' profile.");
        }

        if (!options.Tasks.ContainsKey("chat"))
        {
            errors.Add("AiModels:Tasks must contain a complete 'chat' profile.");
        }

        var usesOllama = false;
        foreach (var pair in options.Tasks)
        {
            var taskId = pair.Key;
            var profile = pair.Value;

            if (!TaskIdPattern().IsMatch(taskId))
            {
                errors.Add(
                    "AiModels:Tasks contains an invalid task identifier. " +
                    "Task IDs must start with a lowercase letter and contain only lowercase letters, digits, or underscores.");
                continue;
            }

            if (profile is null)
            {
                errors.Add($"AiModels:Tasks:{taskId} must be a complete task profile.");
                continue;
            }

            if (!ChatModelProviders.TryNormalize(profile.Provider, out var provider))
            {
                errors.Add(
                    $"AiModels:Tasks:{taskId}:Provider must be Ollama or OpenRouter.");
            }
            else if (provider == ChatModelProviders.OpenRouter)
            {
                errors.Add(
                    $"AiModels:Tasks:{taskId}:Provider selects OpenRouter, which is not available in this build.");
            }
            else
            {
                usesOllama = true;
            }

            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                errors.Add($"AiModels:Tasks:{taskId}:Model is required.");
            }

            ValidatePositive(
                profile.TimeoutSeconds,
                $"AiModels:Tasks:{taskId}:TimeoutSeconds",
                errors);
            ValidatePositive(
                profile.RequestTimeoutSeconds,
                $"AiModels:Tasks:{taskId}:RequestTimeoutSeconds",
                errors);
            ValidatePositive(
                profile.MaxOutputTokens,
                $"AiModels:Tasks:{taskId}:MaxOutputTokens",
                errors);

            if (profile.TimeoutSeconds is > 0
                && profile.RequestTimeoutSeconds is > 0
                && profile.RequestTimeoutSeconds < profile.TimeoutSeconds)
            {
                errors.Add(
                    $"AiModels:Tasks:{taskId}:RequestTimeoutSeconds must be at least TimeoutSeconds.");
            }
        }

        if (usesOllama && !IsValidHttpUrl(_ollamaOptions.BaseUrl))
        {
            errors.Add(
                "Ollama:BaseUrl must be an absolute HTTP or HTTPS URL when an Ollama task is configured.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidatePositive(int? value, string path, ICollection<string> errors)
    {
        if (value is null or <= 0)
        {
            errors.Add($"{path} must be greater than zero.");
        }
    }

    private static bool IsValidHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TaskIdPattern();
}
