namespace ai_assistant_service.Services.Contracts;

public interface IProductApiClient
{
    Task<string> FetchProductContextAsync(string query, CancellationToken cancellationToken);
}
