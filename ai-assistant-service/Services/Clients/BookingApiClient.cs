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
        if (string.IsNullOrWhiteSpace(bookingId))
        {
            // Bookings are now per-user (PENDING/ACCEPTED requests between a requester and a TaskMaster).
            // Without a specific id and without authenticated user context, we cannot list anything safely.
            return "Booking context is per-user. Please provide a specific bookingId.";
        }

        try
        {
            string path = $"/api/booking/{Uri.EscapeDataString(bookingId)}";
            _logger.LogInformation("Fetching booking: {Path}", path);

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
