using ai_assistant_service.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ai_assistant_service.Tests;

public class AiModelOptionsValidationTests
{
    [Fact]
    public async Task HostStartup_FailsWhenChatProfileIsMissing()
    {
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ollama:BaseUrl"] = "http://ollama:11434",
                    ["AiModels:Tasks:synthetic:Provider"] = "Ollama",
                    ["AiModels:Tasks:synthetic:Model"] = "model-a",
                    ["AiModels:Tasks:synthetic:TimeoutSeconds"] = "10",
                    ["AiModels:Tasks:synthetic:RequestTimeoutSeconds"] = "30",
                    ["AiModels:Tasks:synthetic:MaxOutputTokens"] = "128"
                }))
            .ConfigureServices((context, services) =>
                services.AddAiModelRouting(context.Configuration))
            .Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains("chat", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_AcceptsCompleteAllOllamaMapWithAdditionalTask()
    {
        var aiModels = ValidAiModels();
        aiModels.Tasks["synthetic"] = ValidProfile();
        aiModels.Tasks["synthetic"].Model = "model-b";

        var validator = new AiModelsOptionsValidator(
            Options.Create(new OllamaOptions { BaseUrl = "http://ollama:11434" }));
        var result = validator.Validate(Options.DefaultName, aiModels);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Validator_RejectsInvalidProfiles(
        string expectedError,
        Action<AiModelsOptions, OllamaOptions> mutate)
    {
        var aiModels = ValidAiModels();
        var ollama = new OllamaOptions { BaseUrl = "http://ollama:11434" };
        mutate(aiModels, ollama);

        var validator = new AiModelsOptionsValidator(Options.Create(ollama));
        var result = validator.Validate(Options.DefaultName, aiModels);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(expectedError, StringComparison.Ordinal));
    }

    public static TheoryData<string, Action<AiModelsOptions, OllamaOptions>>
        InvalidConfigurations() =>
        new()
        {
            {
                "invalid task identifier",
                (options, _) =>
                {
                    options.Tasks.Remove("chat");
                    options.Tasks["Chat"] = ValidProfile();
                }
            },
            {
                "Provider must be Ollama or OpenRouter",
                (options, _) => options.Tasks["chat"].Provider = "Unknown"
            },
            {
                "OpenRouter, which is not available",
                (options, _) => options.Tasks["chat"].Provider = "OpenRouter"
            },
            {
                "Model is required",
                (options, _) => options.Tasks["chat"].Model = " "
            },
            {
                "RequestTimeoutSeconds must be at least TimeoutSeconds",
                (options, _) => options.Tasks["chat"].RequestTimeoutSeconds = 5
            },
            {
                "AiModels:Tasks:synthetic:MaxOutputTokens",
                (options, _) =>
                {
                    options.Tasks["synthetic"] = ValidProfile();
                    options.Tasks["synthetic"].MaxOutputTokens = 0;
                }
            },
            {
                "Ollama:BaseUrl",
                (_, ollama) => ollama.BaseUrl = "not-a-url"
            }
        };

    private static AiModelsOptions ValidAiModels() =>
        new()
        {
            Tasks = new Dictionary<string, TaskModelOptions>
            {
                ["chat"] = ValidProfile()
            }
        };

    private static TaskModelOptions ValidProfile() =>
        new()
        {
            Provider = "Ollama",
            Model = "qwen3:8b",
            TimeoutSeconds = 10,
            RequestTimeoutSeconds = 30,
            MaxOutputTokens = 128
        };
}
