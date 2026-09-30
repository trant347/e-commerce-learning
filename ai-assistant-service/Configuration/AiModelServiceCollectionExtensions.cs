using ai_assistant_service.Services;
using ai_assistant_service.Services.ChatModels;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Configuration;

public static class AiModelServiceCollectionExtensions
{
    public static IServiceCollection AddAiModelRouting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<OllamaOptions>()
            .Bind(configuration.GetSection(OllamaOptions.SectionName));

        services
            .AddOptions<OpenRouterOptions>()
            .Bind(configuration.GetSection(OpenRouterOptions.SectionName));

        services
            .AddOptions<AiModelsOptions>()
            .Bind(configuration.GetSection(AiModelsOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<AiModelsOptions>, AiModelsOptionsValidator>();
        services.AddSingleton<TaskModelProfileCatalog>();
        services.AddSingleton<ITaskChatModelResolver, TaskChatModelResolver>();

        services.AddOptions<PromptOptions>()
            .Bind(configuration.GetSection(PromptOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PromptOptions>, PromptOptionsValidator>();
        services.AddSingleton<AssistantPrompt>();

        services.AddHttpClient<OllamaChatModelClient>((serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
            if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseAddress))
            {
                httpClient.BaseAddress = baseAddress;
            }
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton(provider => new ChatModelClientRegistration(
            ChatModelProviders.Ollama,
            provider.GetRequiredService<OllamaChatModelClient>()));

        services.AddHttpClient<OpenRouterChatModelClient>(httpClient =>
        {
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton(provider => new ChatModelClientRegistration(
            ChatModelProviders.OpenRouter,
            provider.GetRequiredService<OpenRouterChatModelClient>()));

        return services;
    }
}
