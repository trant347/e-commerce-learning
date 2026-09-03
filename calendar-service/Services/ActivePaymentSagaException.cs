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

        /// <summary>
        /// Used by the pre-flight duplicate check, which can phrase the conflict in terms the
        /// requester understands rather than exposing saga vocabulary.
        /// </summary>
        public ActivePaymentSagaException(string bookingId, string operation, string message)
            : base(message)
        {
            BookingId = bookingId;
            Operation = operation;
        }

        public string BookingId { get; }
        public string Operation { get; }
    }
}
