using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services.Tools;

public sealed class GetBookingsTool : IToolDefinition
{
    private readonly IBookingApiClient _client;

    public string Name => "get_bookings";

    public string Description =>
        "Retrieve booking appointments from the calendar service. " +
        "Use this when the user asks about their scheduled appointments, upcoming bookings, or booking history. " +
        "Optionally fetch a specific booking by its ID, or retrieve all bookings when no ID is provided.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            booking_id = new
            {
                type = "string",
                description = "The ID of a specific booking to look up. Leave empty to retrieve all bookings."
            }
        },
        required = Array.Empty<string>()
    };

    public GetBookingsTool(IBookingApiClient client)
    {
        _client = client;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken)
    {
        arguments.TryGetValue("booking_id", out var bookingId);
        return await _client.FetchBookingContextAsync(bookingId, cancellationToken);
    }
}
