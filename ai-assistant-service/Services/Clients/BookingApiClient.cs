using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services.Clients;

public sealed class BookingApiClient : IBookingApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BookingApiClient> _logger;

    public BookingApiClient(HttpClient httpClient, ILogger<BookingApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> FetchBookingContextAsync(string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return "No user id provided, booking lookup skipped.";
        }

        try
        {
            var encodedUserId = Uri.EscapeDataString(userId);
            var response = await _httpClient.GetAsync($"/api/bookings/status?userId={encodedUserId}", cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch booking context from booking-service");
            return "Booking tool unavailable.";
        }
    }
}
