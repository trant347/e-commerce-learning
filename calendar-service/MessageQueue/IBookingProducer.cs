namespace calendar_service.MessageQueue
{
    public interface IBookingProducer<Tk, Tv> : IDisposable
    {
        public Task ProduceBookingAsync(Tk key, Tv value);
    }
}
