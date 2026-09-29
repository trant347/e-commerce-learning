using ai_assistant_service.Services.ChatModels;
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

        return services;
    }
}
