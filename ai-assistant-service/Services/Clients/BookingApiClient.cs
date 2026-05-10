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

    public async Task<string> FetchBookingContextAsync(string? bookingId, CancellationToken cancellationToken)
    {
        try
        {
            // Fetch a specific booking by ID, or all bookings when no ID is given
            string path = string.IsNullOrWhiteSpace(bookingId)
                ? "/api/booking"
                : $"/api/booking/{Uri.EscapeDataString(bookingId)}";

            _logger.LogInformation("Fetching bookings: {Path}", path);

            var response = await _httpClient.GetAsync(path, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to fetch booking context from calendar-service");
            return "Booking data unavailable.";
        }
    }
}
