using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services.Clients;

public sealed class ProductApiClient : IProductApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProductApiClient> _logger;

    public ProductApiClient(HttpClient httpClient, ILogger<ProductApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> FetchProductContextAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            // Fetch all task masters and return as context
            // The AI will filter based on the user's query
            var response = await _httpClient.GetAsync("/products", cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch task master context from product-service");
            return "Task master data unavailable.";
        }
    }
}
