namespace calendar_service.MessageQueue
{
    public sealed record BookingNotification(
        string Type,
        string RecipientUsername,
        string Message,
        string ActionType,
        Dictionary<string, string> ActionPayload);
}
