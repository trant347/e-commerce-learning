using Payment.Contracts.V1;

namespace payment_service.MessageQueue
{
    public interface IPaymentResultProducer
    {
        Task PublishAsync(
            PaymentResultV1 result,
            string? traceParent,
            CancellationToken cancellationToken);
    }
}
