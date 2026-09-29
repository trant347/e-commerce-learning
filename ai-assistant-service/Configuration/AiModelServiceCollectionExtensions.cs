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
            .AddOptions<AiModelsOptions>()
            .Bind(configuration.GetSection(AiModelsOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<AiModelsOptions>, AiModelsOptionsValidator>();
        services.AddSingleton<TaskModelProfileCatalog>();
        services.AddSingleton<ITaskChatModelResolver, TaskChatModelResolver>();

        services.AddHttpClient<OllamaChatModelClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl!);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton(provider => new ChatModelClientRegistration(
            ChatModelProviders.Ollama,
            provider.GetRequiredService<OllamaChatModelClient>()));

        return services;
    }
}
