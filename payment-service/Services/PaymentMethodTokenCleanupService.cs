using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using payment_service.Data;

namespace payment_service.Services
{
    public class PaymentMethodTokenCleanupService : IPaymentMethodTokenCleanupService
    {
        private readonly PaymentDbContext _dbContext;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _retention;

        public PaymentMethodTokenCleanupService(
            PaymentDbContext dbContext,
            TimeProvider timeProvider,
            IOptions<PaymentMethodTokenOptions> options)
        {
            _dbContext = dbContext;
            _timeProvider = timeProvider;

            if (options.Value.RetentionSeconds < 0)
            {
                throw new InvalidOperationException("PaymentMethodTokens:RetentionSeconds cannot be negative.");
            }

            _retention = TimeSpan.FromSeconds(options.Value.RetentionSeconds);
        }

        public async Task<int> DeleteRetainedTokensAsync(
            CancellationToken cancellationToken = default)
        {
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime.Subtract(_retention);
            var tokens = _dbContext.PaymentMethodTokens.Where(token =>
                token.ExpiresAt <= cutoff
                || (token.RedeemedAt != null && token.RedeemedAt <= cutoff));

            if (_dbContext.Database.IsRelational())
            {
                return await tokens.ExecuteDeleteAsync(cancellationToken);
            }

            var retained = await tokens.ToListAsync(cancellationToken);
            _dbContext.PaymentMethodTokens.RemoveRange(retained);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return retained.Count;
        }
    }
}
