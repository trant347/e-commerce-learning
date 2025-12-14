namespace notification_service.Services
{
    public record NotificationStreamedEvent(string Id, string Message, DateTime Timestamp);  
}