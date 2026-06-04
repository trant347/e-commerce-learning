using System.Diagnostics.Metrics;
using System.Text.Json;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Caching.Distributed;

namespace ai_assistant_service.Services.Clients;

public sealed class ProductApiClient : IProductApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProductApiClient> _logger;
    private readonly IDistributedCache _cache;

    private static readonly TimeSpan ProductCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CategoriesCacheTtl = TimeSpan.FromMinutes(30);

    private static readonly Meter CacheMeter = new("AiAssistant.Cache", "1.0");
    private static readonly Counter<long> CacheHits = CacheMeter.CreateCounter<long>("cache.hits", description: "Cache hit count");
    private static readonly Counter<long> CacheMisses = CacheMeter.CreateCounter<long>("cache.misses", description: "Cache miss count");
    private static readonly Counter<long> CacheErrors = CacheMeter.CreateCounter<long>("cache.errors", description: "Cache error count");

    public ProductApiClient(HttpClient httpClient, ILogger<ProductApiClient> logger, IDistributedCache cache)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cache = cache;
    }

    public async Task<string> FetchProductContextAsync(string? category, string? location, CancellationToken cancellationToken)
    {
        string path;
        if (!string.IsNullOrWhiteSpace(category))
            path = $"/products?category={Uri.EscapeDataString(category)}";
        else if (!string.IsNullOrWhiteSpace(location))
            path = $"/products?location={Uri.EscapeDataString(location)}";
        else
            path = "/products";

        string cacheKey = $"products:{path}";

        // Try cache first
        try
        {
            var cached = await _cache.GetStringAsync(cacheKey, cancellationToken);
            if (cached != null)
            {
                _logger.LogDebug("[Cache] HIT ai-assistant key={CacheKey}", cacheKey);
                CacheHits.Add(1, new KeyValuePair<string, object?>("type", "products"));
                return cached;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Cache] Redis read failed for key={CacheKey}, falling through to HTTP", cacheKey);
            CacheErrors.Add(1, new KeyValuePair<string, object?>("type", "read"));
        }

        // Cache miss — call product-service
        CacheMisses.Add(1, new KeyValuePair<string, object?>("type", "products"));
        try
        {
            _logger.LogInformation("Fetching task masters: {Path}", path);
            var response = await _httpClient.GetAsync(path, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Cache the response
            try
            {
                await _cache.SetStringAsync(cacheKey, body, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ProductCacheTtl
                }, cancellationToken);
                _logger.LogDebug("[Cache] STORED ai-assistant key={CacheKey}", cacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Cache] Redis write failed for key={CacheKey}", cacheKey);
                CacheErrors.Add(1, new KeyValuePair<string, object?>("type", "write"));
            }

            return body;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch task master context from product-service");
            return "Task master data unavailable.";
        }
    }

    public async Task<string[]> FetchCategoriesAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = "products:categories";

        // Try cache first
        try
        {
            var cached = await _cache.GetStringAsync(cacheKey, cancellationToken);
            if (cached != null)
            {
                _logger.LogDebug("[Cache] HIT ai-assistant key={CacheKey}", cacheKey);
                CacheHits.Add(1, new KeyValuePair<string, object?>("type", "categories"));
                return JsonSerializer.Deserialize<string[]>(cached) ?? [];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Cache] Redis read failed for key={CacheKey}, falling through to HTTP", cacheKey);
            CacheErrors.Add(1, new KeyValuePair<string, object?>("type", "read"));
        }

        // Cache miss — call product-service
        CacheMisses.Add(1, new KeyValuePair<string, object?>("type", "categories"));
        try
        {
            _logger.LogInformation("Fetching categories from product-service");
            var response = await _httpClient.GetAsync("/products/categories", cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            // Cache the response
            try
            {
                await _cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = CategoriesCacheTtl
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Cache] Redis write failed for key={CacheKey}", cacheKey);
                CacheErrors.Add(1, new KeyValuePair<string, object?>("type", "write"));
            }

            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch categories from product-service");
            return [];
        }
    }
}
