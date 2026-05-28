namespace calendar_service.MessageQueue
{
    public interface INotificationProducer
    {
        Task PublishAsync(object payload);
    }
}
