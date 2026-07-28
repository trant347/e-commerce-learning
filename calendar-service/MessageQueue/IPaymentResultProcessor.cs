using Payment.Contracts.V1;

namespace calendar_service.MessageQueue
{
    public enum PaymentResultProcessingOutcome
    {
        Applied,
        Declined,
        Mismatched,
        Duplicate
    }

    public interface IPaymentResultProcessor
    {
        Task<PaymentResultProcessingOutcome> ProcessAsync(
            PaymentResultV1 result,
            CancellationToken cancellationToken = default);
    }
}
