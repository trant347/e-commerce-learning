namespace ai_assistant_service.Services.Contracts;

public interface IOllamaClient
{
    Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}
