namespace ai_assistant_service.Services.Contracts;

public interface IProductApiClient
{
    /// <summary>
    /// Fetches task master context for the AI prompt.
    /// Filters by category and/or location when provided.
    /// </summary>
    Task<string> FetchProductContextAsync(string? category, string? location, CancellationToken cancellationToken);
}
