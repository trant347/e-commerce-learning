namespace payment_service.Services
{
    public interface IPaymentMethodTokenCleanupService
    {
        Task<int> DeleteRetainedTokensAsync(CancellationToken cancellationToken = default);
    }
}
