using payment_service.Models;

namespace payment_service.MessageQueue
{
    public interface IPaymentResultOutboxStore
    {
        Task<int> ReconcileMissingAsync(
            CancellationToken cancellationToken);

        Task<int> GetPendingCountAsync(
            CancellationToken cancellationToken);

        Task<PaymentResultOutbox?> TryClaimNextAsync(
            TimeSpan claimLease,
            CancellationToken cancellationToken);

        Task<bool> MarkDispatchedAsync(
            Guid outboxId,
            DateTime claimTimestamp,
            CancellationToken cancellationToken);

        Task<bool> RescheduleAsync(
            Guid outboxId,
            DateTime claimTimestamp,
            DateTime nextAttemptAt,
            string error,
            CancellationToken cancellationToken);
    }
}
