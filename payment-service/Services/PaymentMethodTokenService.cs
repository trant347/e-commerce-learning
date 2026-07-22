using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using payment_service.Contracts;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    public class PaymentMethodTokenService : IPaymentMethodTokenService
    {
        private const string TokenPrefix = "pmt_";

        private readonly PaymentDbContext _dbContext;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _tokenLifetime;

        public PaymentMethodTokenService(
            PaymentDbContext dbContext,
            IOptions<PaymentMethodTokenOptions> options,
            TimeProvider timeProvider)
        {
            _dbContext = dbContext;
            _timeProvider = timeProvider;

            if (options.Value.LifetimeSeconds <= 0)
            {
                throw new InvalidOperationException("PaymentMethodTokens:LifetimeSeconds must be greater than zero.");
            }

            _tokenLifetime = TimeSpan.FromSeconds(options.Value.LifetimeSeconds);
        }

        public async Task<PaymentMethodTokenResponse> TokenizeAsync(
            CreditCardInfo creditCard,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(creditCard);

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var cardNumber = PaymentCardUtility.ValidateAndNormalizeNumber(creditCard.CardNumber);
            PaymentCardUtility.ValidateExpiryDate(creditCard.ExpiryDate, now);
            PaymentCardUtility.ValidateCvv(creditCard.CVV);
            var ownerName = PaymentCardUtility.ValidateOwnerName(creditCard.OwnerName);

            var token = CreateOpaqueToken();
            var expiresAt = now.Add(_tokenLifetime);
            _dbContext.PaymentMethodTokens.Add(new PaymentMethodTokenRecord
            {
                TokenHash = HashToken(token),
                MaskedCardNumber = PaymentCardUtility.Mask(cardNumber),
                OwnerName = ownerName,
                SimulatesDecline = cardNumber == WalletSimulationPaymentGateway.SimulatedDeclineCardNumber,
                CreatedAt = now,
                ExpiresAt = expiresAt
            });
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new PaymentMethodTokenResponse
            {
                PaymentMethodToken = token,
                ExpiresAt = expiresAt
            };
        }

        /// <summary>
        /// Consumes a valid payment-method token exactly once and returns only the
        /// non-sensitive metadata needed to process the simulated payment.
        /// This operation does not charge the card.
        /// </summary>
        public async Task<RedeemedPaymentMethod> RedeemAsync(
            string paymentMethodToken,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(paymentMethodToken))
            {
                throw InvalidToken();
            }

            var tokenHash = HashToken(paymentMethodToken);
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            if (_dbContext.Database.IsRelational())
            {
                // Validate and redeem in one conditional update so concurrent consumers
                // cannot both successfully use the same token.
                var updated = await _dbContext.PaymentMethodTokens
                    .Where(token => token.TokenHash == tokenHash
                        && token.RedeemedAt == null
                        && token.ExpiresAt > now)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(token => token.RedeemedAt, now),
                        cancellationToken);

                if (updated == 0)
                {
                    await ThrowRedemptionFailureAsync(tokenHash, now, cancellationToken);
                }

                var redeemed = await _dbContext.PaymentMethodTokens
                    .AsNoTracking()
                    .SingleAsync(token => token.TokenHash == tokenHash, cancellationToken);
                return ToRedeemedPaymentMethod(redeemed);
            }

            var record = await _dbContext.PaymentMethodTokens
                .SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);
            ValidateRedeemable(record, now);
            record!.RedeemedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return ToRedeemedPaymentMethod(record);
        }

        private async Task ThrowRedemptionFailureAsync(
            string tokenHash,
            DateTime now,
            CancellationToken cancellationToken)
        {
            var record = await _dbContext.PaymentMethodTokens
                .AsNoTracking()
                .SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);
            ValidateRedeemable(record, now);
            throw InvalidToken();
        }

        private static void ValidateRedeemable(PaymentMethodTokenRecord? record, DateTime now)
        {
            if (record == null)
            {
                throw InvalidToken();
            }

            if (record.RedeemedAt.HasValue)
            {
                throw new PaymentMethodTokenException(
                    PaymentMethodTokenException.AlreadyRedeemed,
                    "Payment method token has already been used.");
            }

            if (record.ExpiresAt <= now)
            {
                throw new PaymentMethodTokenException(
                    PaymentMethodTokenException.Expired,
                    "Payment method token has expired.");
            }
        }

        private static RedeemedPaymentMethod ToRedeemedPaymentMethod(PaymentMethodTokenRecord record) =>
            new(record.MaskedCardNumber, record.OwnerName, record.SimulatesDecline);

        private static PaymentMethodTokenException InvalidToken() =>
            new(PaymentMethodTokenException.Invalid, "Payment method token is invalid.");

        private static string CreateOpaqueToken()
        {
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            return TokenPrefix + Convert.ToBase64String(tokenBytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string HashToken(string token)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(hash);
        }
    }
}
