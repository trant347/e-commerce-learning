namespace calendar_service.Services
{
    public class ActivePaymentSagaException : InvalidOperationException
    {
        public ActivePaymentSagaException(string bookingId, string operation)
            : base($"An active {operation} saga already exists for booking '{bookingId}'.")
        {
            BookingId = bookingId;
            Operation = operation;
        }

        public string BookingId { get; }
        public string Operation { get; }
    }
}
