namespace ai_assistant_service.Services.Contracts;

public interface IProductApiClient
{
    /// <summary>
    /// Fetches task master context for the AI prompt.
    /// Filters by category and/or location when provided.
    /// </summary>
    Task<string> FetchProductContextAsync(string? category, string? location, CancellationToken cancellationToken);

    /// <summary>
    /// Returns all distinct job category strings stored across task masters.
    /// Used to populate the tool schema enum so the LLM normalises user input.
    /// </summary>
    Task<string[]> FetchCategoriesAsync(CancellationToken cancellationToken);
}
