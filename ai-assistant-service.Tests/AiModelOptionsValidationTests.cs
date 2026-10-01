using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace ai_assistant_service.Tests;

public class AiModelOptionsValidationTests
{
    [Theory]
    [InlineData("Model", "", "AiModels:Tasks:synthetic:Model")]
    [InlineData("Provider", "Unknown", "AiModels:Tasks:synthetic:Provider")]
    [InlineData("MaxOutputTokens", "0", "AiModels:Tasks:synthetic:MaxOutputTokens")]
    [InlineData("RequestTimeoutSeconds", "", "AiModels:Tasks:synthetic:RequestTimeoutSeconds")]
    [InlineData("key", "", "OpenRouter:ApiKey")]
    [InlineData("url", "invalid", "OpenRouter:BaseUrl")]
    public async Task HostStartup_ValidatesUnusedOpenRouterProfileAndSharedSettings(
        string setting, string value, string expectedError)
    {
        var values = new Dictionary<string, string?>
        {
            ["AiModels:Tasks:synthetic:Provider"] = "OpenRouter",
            ["AiModels:Tasks:synthetic:Model"] = "vendor/synthetic",
            ["AiModels:Tasks:synthetic:TimeoutSeconds"] = "10",
            ["AiModels:Tasks:synthetic:RequestTimeoutSeconds"] = "30",
            ["AiModels:Tasks:synthetic:MaxOutputTokens"] = "128",
            ["OpenRouter:ApiKey"] = "fake-key",
            ["OpenRouter:BaseUrl"] = "https://openrouter.test/api/v1"
        };
        values[setting switch
        {
            "key" => "OpenRouter:ApiKey",
            "url" => "OpenRouter:BaseUrl",
            _ => $"AiModels:Tasks:synthetic:{setting}"
        }] = value;
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json")
                .AddInMemoryCollection(values))
            .ConfigureServices((context, services) => services.AddAiModelRouting(context.Configuration))
            .Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fake-key", exception.Message);
    }

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

    [Theory]
    [InlineData("all-ollama")]
    [InlineData("openrouter-only")]
    [InlineData("mixed-openrouter-chat")]
    [InlineData("mixed-ollama-chat")]
    public async Task DeploymentRecipeEnvironment_StartsAndResolvesEachTaskProvider(string recipe)
    {
        // Mirrors the container environment rendered by the recipes in
        // docs/AI_ASSISTANT_DEPLOYMENT.md, with fake secrets and synthetic tasks.
        var environment = new Dictionary<string, string?>
        {
            ["Ollama__BaseUrl"] = "http://ollama:11434",
            ["AiModels__Tasks__chat__Provider"] = "Ollama",
            ["AiModels__Tasks__chat__Model"] = "qwen3:8b",
            ["AiModels__Tasks__chat__TimeoutSeconds"] = "600",
            ["AiModels__Tasks__chat__RequestTimeoutSeconds"] = "3000",
            ["AiModels__Tasks__chat__MaxOutputTokens"] = "1024",
            ["OpenRouter__ApiKey"] = "",
            ["OpenRouter__HttpReferer"] = ""
        };
        var openRouterChat = new Dictionary<string, string?>
        {
            ["AiModels__Tasks__chat__Provider"] = "OpenRouter",
            ["AiModels__Tasks__chat__Model"] = "vendor/fake-model",
            ["AiModels__Tasks__chat__TimeoutSeconds"] = "60",
            ["AiModels__Tasks__chat__RequestTimeoutSeconds"] = "300",
            ["AiModels__Tasks__chat__MaxOutputTokens"] = "1024",
            ["OpenRouter__ApiKey"] = "fake-test-key"
        };
        var expected = new Dictionary<string, string> { ["chat"] = "Ollama" };

        switch (recipe)
        {
            case "openrouter-only":
                Merge(environment, openRouterChat);
                expected["chat"] = "OpenRouter";
                break;
            case "mixed-openrouter-chat":
                Merge(environment, openRouterChat);
                Merge(environment, SyntheticTask("synthetic_local", "Ollama", "qwen3:8b"));
                expected["chat"] = "OpenRouter";
                expected["synthetic_local"] = "Ollama";
                break;
            case "mixed-ollama-chat":
                Merge(environment, SyntheticTask("synthetic_remote", "OpenRouter", "vendor/fake-model"));
                environment["OpenRouter__ApiKey"] = "fake-test-key";
                expected["synthetic_remote"] = "OpenRouter";
                break;
        }

        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json")
                .AddInMemoryCollection(environment.ToDictionary(
                    pair => pair.Key.Replace("__", ":", StringComparison.Ordinal),
                    pair => pair.Value)))
            .ConfigureServices((context, services) =>
                services.AddAiModelRouting(context.Configuration))
            .Build();

        await host.StartAsync();
        var resolver = host.Services.GetRequiredService<ITaskChatModelResolver>();
        foreach (var (taskId, provider) in expected)
        {
            Assert.Equal(provider, resolver.Resolve(taskId).Profile.Provider);
        }
        await host.StopAsync();

        static void Merge(Dictionary<string, string?> target, Dictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                target[key] = value;
            }
        }

        static Dictionary<string, string?> SyntheticTask(string taskId, string provider, string model) => new()
        {
            [$"AiModels__Tasks__{taskId}__Provider"] = provider,
            [$"AiModels__Tasks__{taskId}__Model"] = model,
            [$"AiModels__Tasks__{taskId}__TimeoutSeconds"] = "60",
            [$"AiModels__Tasks__{taskId}__RequestTimeoutSeconds"] = "300",
            [$"AiModels__Tasks__{taskId}__MaxOutputTokens"] = "256"
        };
    }

    [Fact]
    public void Validator_AcceptsCompleteAllOllamaMapWithAdditionalTask()
    {
        var aiModels = ValidAiModels();
        aiModels.Tasks["synthetic"] = ValidProfile();
        aiModels.Tasks["synthetic"].Model = "model-b";

        var validator = new AiModelsOptionsValidator(
            Options.Create(new OllamaOptions { BaseUrl = "http://ollama:11434" }),
            Options.Create(new OpenRouterOptions()));
        var result = validator.Validate(Options.DefaultName, aiModels);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_AcceptsMixedProviderMapWithSharedOpenRouterSettings()
    {
        var aiModels = ValidAiModels();
        aiModels.Tasks["synthetic"] = ValidProfile();
        aiModels.Tasks["synthetic"].Provider = "OpenRouter";
        aiModels.Tasks["synthetic"].Model = "vendor/tool-model";

        var validator = new AiModelsOptionsValidator(
            Options.Create(new OllamaOptions { BaseUrl = "http://ollama:11434" }),
            Options.Create(ValidOpenRouter()));
        var result = validator.Validate(Options.DefaultName, aiModels);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Validator_RejectsInvalidProfiles(
        string expectedError,
        Action<AiModelsOptions, OllamaOptions, OpenRouterOptions> mutate)
    {
        var aiModels = ValidAiModels();
        var ollama = new OllamaOptions { BaseUrl = "http://ollama:11434" };
        var openRouter = ValidOpenRouter();
        mutate(aiModels, ollama, openRouter);

        var validator = new AiModelsOptionsValidator(
            Options.Create(ollama),
            Options.Create(openRouter));
        var result = validator.Validate(Options.DefaultName, aiModels);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(expectedError, StringComparison.Ordinal));
    }

    public static TheoryData<string, Action<AiModelsOptions, OllamaOptions, OpenRouterOptions>>
        InvalidConfigurations() =>
        new()
        {
            {
                "invalid task identifier",
                (options, _, _) =>
                {
                    options.Tasks.Remove("chat");
                    options.Tasks["Chat"] = ValidProfile();
                }
            },
            {
                "Provider must be Ollama or OpenRouter",
                (options, _, _) => options.Tasks["chat"].Provider = "Unknown"
            },
            {
                "Model is required",
                (options, _, _) => options.Tasks["chat"].Model = " "
            },
            {
                "RequestTimeoutSeconds must be at least TimeoutSeconds",
                (options, _, _) => options.Tasks["chat"].RequestTimeoutSeconds = 5
            },
            {
                "AiModels:Tasks:synthetic:MaxOutputTokens",
                (options, _, _) =>
                {
                    options.Tasks["synthetic"] = ValidProfile();
                    options.Tasks["synthetic"].MaxOutputTokens = 0;
                }
            },
            {
                "Ollama:BaseUrl",
                (_, ollama, _) => ollama.BaseUrl = "not-a-url"
            },
            {
                "OpenRouter:BaseUrl",
                (options, _, openRouter) =>
                {
                    UseOpenRouter(options);
                    openRouter.BaseUrl = "not-a-url";
                }
            },
            {
                "OpenRouter:ApiKey",
                (options, _, openRouter) =>
                {
                    UseOpenRouter(options);
                    openRouter.ApiKey = " ";
                }
            },
            {
                "OpenRouter:HttpReferer",
                (options, _, openRouter) =>
                {
                    UseOpenRouter(options);
                    openRouter.HttpReferer = "not-a-url";
                }
            },
            {
                "OpenRouter:AppTitle",
                (options, _, openRouter) =>
                {
                    UseOpenRouter(options);
                    openRouter.HttpReferer = "https://taskmaster.example";
                    openRouter.AppTitle = " ";
                }
            },
            {
                "OpenRouter:RequireParameters",
                (options, _, openRouter) =>
                {
                    UseOpenRouter(options);
                    openRouter.RequireParameters = false;
                }
            },
            {
                "OpenRouter:DataCollection",
                (options, _, openRouter) =>
                {
                    UseOpenRouter(options);
                    openRouter.DataCollection = "allow";
                }
            },
            {
                "OpenRouter:MaxRetries",
                (options, _, openRouter) =>
                {
                    UseOpenRouter(options);
                    openRouter.MaxRetries = 2;
                }
            }
        };

    private static void UseOpenRouter(AiModelsOptions options)
    {
        options.Tasks["chat"].Provider = "OpenRouter";
        options.Tasks["chat"].Model = "vendor/tool-model";
    }

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

    private static OpenRouterOptions ValidOpenRouter() =>
        new()
        {
            BaseUrl = "https://openrouter.ai/api/v1",
            ApiKey = "test-key",
            AppTitle = "TaskMaster Hub",
            AllowFallbacks = true,
            RequireParameters = true,
            DataCollection = "deny",
            MaxRetries = 1
        };
}
