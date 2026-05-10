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

    public async Task<string> FetchProductContextAsync(string? category, string? location, CancellationToken cancellationToken)
    {
        try
        {
            // Use specific filter endpoints when arguments are provided
            string path;
            if (!string.IsNullOrWhiteSpace(category))
                path = $"/products?category={Uri.EscapeDataString(category)}";
            else if (!string.IsNullOrWhiteSpace(location))
                path = $"/products?location={Uri.EscapeDataString(location)}";
            else
                path = "/products";

            _logger.LogInformation("Fetching task masters: {Path}", path);

            var response = await _httpClient.GetAsync(path, cancellationToken);
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
