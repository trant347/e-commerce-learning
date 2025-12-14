namespace worker_service.MessageQueue
{
    public interface INotificationProducer<Tk,Tv> : IDisposable
    {
        Task ProduceNotificationAsync(Tk key, Tv value, CancellationToken cancellationToken = default);
    }
}
