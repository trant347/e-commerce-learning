using Payment.Contracts.V1;

namespace calendar_service.MessageQueue
{
    public interface IPaymentRequestProducer
    {
        Task PublishAsync(
            PaymentRequestedV1 request,
            string? traceParent,
            CancellationToken cancellationToken);
    }
}
