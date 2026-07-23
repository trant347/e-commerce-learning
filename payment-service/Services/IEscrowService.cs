using payment_service.Models;

namespace payment_service.Services
{
    public interface IEscrowService
    {
        Task<EscrowRecord> CreateAsync(
            Guid escrowId,
            string bookingId,
            decimal amount,
            string currency,
            string requesterUserId,
            string taskMasterUserId,
            string custodyUserId,
            CancellationToken cancellationToken = default);

        Task<EscrowRecord?> GetByIdAsync(
            Guid escrowId,
            CancellationToken cancellationToken = default);

        Task<EscrowRecord?> GetByBookingIdAsync(
            string bookingId,
            CancellationToken cancellationToken = default);

        Task<EscrowRecord> MarkFundedAsync(
            Guid escrowId,
            Guid transactionId,
            CancellationToken cancellationToken = default);

        Task<EscrowRecord> MarkReleasedAsync(
            Guid escrowId,
            Guid transactionId,
            CancellationToken cancellationToken = default);

        Task<EscrowRecord> MarkRefundedAsync(
            Guid escrowId,
            Guid transactionId,
            CancellationToken cancellationToken = default);
    }
}
