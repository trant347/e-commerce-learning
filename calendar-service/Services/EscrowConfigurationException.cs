namespace calendar_service.Services
{
    public sealed class EscrowConfigurationException : Exception
    {
        public EscrowConfigurationException(string message)
            : base(message)
        {
        }
    }
}
