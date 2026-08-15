using payment_service.Models;

namespace payment_service.Services
{
    public interface ILedgerCutoverService
    {
        Task<LedgerCutoverState> ExecuteAsync(
            CancellationToken cancellationToken = default);

        Task VerifyAsync(
            LedgerCutoverState state,
            CancellationToken cancellationToken = default);
    }
}
