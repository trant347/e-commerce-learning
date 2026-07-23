namespace calendar_service.Services
{
    public class SagaOutboxPersistenceException : Exception
    {
        public SagaOutboxPersistenceException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
