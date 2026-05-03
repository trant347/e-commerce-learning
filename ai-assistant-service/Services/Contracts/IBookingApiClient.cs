namespace ai_assistant_service.Services.Contracts;

public interface IBookingApiClient
{
    Task<string> FetchBookingContextAsync(string? userId, CancellationToken cancellationToken);
}
