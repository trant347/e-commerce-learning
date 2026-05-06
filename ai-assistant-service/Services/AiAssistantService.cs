using System.Text;
using ai_assistant_service.Contracts;
using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services;

public sealed class AiAssistantService : IAiAssistantService
{
    private readonly IConfiguration _configuration;
    private readonly IOllamaClient _ollamaClient;
    private readonly IProductApiClient _productApiClient;
    private readonly IBookingApiClient _bookingApiClient;

    public AiAssistantService(
        IConfiguration configuration,
        IOllamaClient ollamaClient,
        IProductApiClient productApiClient,
        IBookingApiClient bookingApiClient)
    {
        _configuration = configuration;
        _ollamaClient = ollamaClient;
        _productApiClient = productApiClient;
        _bookingApiClient = bookingApiClient;
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var model = _configuration["Ollama:Model"] ?? "llama3.1:8b";
        var systemPrompt = _configuration["PromptOptions:SystemPrompt"]
            ?? "You are an e-commerce assistant. Use factual data from tools.";

        var productContext = ""; // await _productApiClient.FetchProductContextAsync(request.Message, cancellationToken);
        var bookingContext = ""; //await _bookingApiClient.FetchBookingContextAsync(request.UserId, cancellationToken);

        var userPrompt = BuildUserPrompt(request, productContext, bookingContext);
        var answer = await _ollamaClient.GenerateAsync(model, systemPrompt, userPrompt, cancellationToken);

        return new ChatResponse
        {
            Answer = answer,
            Model = model,
            Sources =
            [
                "product-service",
                "booking-service"
            ]
        };
    }

    private static string BuildUserPrompt(ChatRequest request, string productContext, string bookingContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine("User question:");
        sb.AppendLine(request.Message);
        sb.AppendLine();
        sb.AppendLine("Tool output from product-service:");
        sb.AppendLine(productContext);
        sb.AppendLine();
        sb.AppendLine("Tool output from booking-service:");
        sb.AppendLine(bookingContext);
        sb.AppendLine();
        sb.AppendLine("Answer with concise, factual details. If tool output is unavailable, state that clearly.");

        return sb.ToString();
    }
}
