using Payment.Contracts.V1;

namespace payment_service.Services
{
    public interface IPaymentRequestProcessor
    {
        Task<PaymentResultV1> ProcessAsync(
            PaymentRequestedV1 request,
            CancellationToken cancellationToken = default);
    }
}
