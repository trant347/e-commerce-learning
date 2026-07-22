using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using payment_service.Contracts;
using payment_service.Data;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class PaymentMethodTokenServiceTests
    {
        private static readonly DateTimeOffset InitialTime =
            new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

        private static PaymentDbContext NewInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new PaymentDbContext(options);
        }

        private static CreditCardInfo ValidCard(string cardNumber = "4111111111111111") => new()
        {
            CardNumber = cardNumber,
            ExpiryDate = "12/30",
            CVV = "123",
            OwnerName = "Jane Doe"
        };

        private static PaymentMethodTokenService NewService(
            PaymentDbContext dbContext,
            TestTimeProvider timeProvider,
            int lifetimeSeconds = 300) =>
            new(
                dbContext,
                Options.Create(new PaymentMethodTokenOptions { LifetimeSeconds = lifetimeSeconds }),
                timeProvider);

        [Fact]
        public async Task TokenizeAndRedeem_ValidCard_ReturnsOpaqueSingleUseToken()
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext, new TestTimeProvider(InitialTime));

            var token = await service.TokenizeAsync(ValidCard());
            var redeemed = await service.RedeemAsync(token.PaymentMethodToken);

            Assert.StartsWith("pmt_", token.PaymentMethodToken);
            Assert.Equal("************1111", redeemed.MaskedCardNumber);
            Assert.Equal("Jane Doe", redeemed.OwnerName);
            Assert.False(redeemed.SimulatesDecline);
        }

        [Theory]
        [InlineData("4111111111111112", "12/30", "123", "Jane Doe")]
        [InlineData("4111111111111111", "12/20", "123", "Jane Doe")]
        [InlineData("4111111111111111", "12/30", "12", "Jane Doe")]
        [InlineData("4111111111111111", "12/30", "123", "")]
        public async Task Tokenize_InvalidCardData_IsRejected(
            string cardNumber,
            string expiryDate,
            string cvv,
            string ownerName)
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext, new TestTimeProvider(InitialTime));
            var card = new CreditCardInfo
            {
                CardNumber = cardNumber,
                ExpiryDate = expiryDate,
                CVV = cvv,
                OwnerName = ownerName
            };

            await Assert.ThrowsAsync<ArgumentException>(() => service.TokenizeAsync(card));
            Assert.Empty(dbContext.PaymentMethodTokens);
        }

        [Fact]
        public async Task Redeem_ExpiredToken_IsRejected()
        {
            await using var dbContext = NewInMemoryContext();
            var timeProvider = new TestTimeProvider(InitialTime);
            var service = NewService(dbContext, timeProvider, lifetimeSeconds: 60);
            var token = await service.TokenizeAsync(ValidCard());
            timeProvider.Advance(TimeSpan.FromSeconds(61));

            var exception = await Assert.ThrowsAsync<PaymentMethodTokenException>(
                () => service.RedeemAsync(token.PaymentMethodToken));

            Assert.Equal(PaymentMethodTokenException.Expired, exception.Code);
        }

        [Fact]
        public async Task Redeem_ReusedToken_IsRejected()
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext, new TestTimeProvider(InitialTime));
            var token = await service.TokenizeAsync(ValidCard());
            await service.RedeemAsync(token.PaymentMethodToken);

            var exception = await Assert.ThrowsAsync<PaymentMethodTokenException>(
                () => service.RedeemAsync(token.PaymentMethodToken));

            Assert.Equal(PaymentMethodTokenException.AlreadyRedeemed, exception.Code);
        }

        [Fact]
        public async Task Redeem_UnknownToken_IsRejected()
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext, new TestTimeProvider(InitialTime));

            var exception = await Assert.ThrowsAsync<PaymentMethodTokenException>(
                () => service.RedeemAsync("pmt_unknown"));

            Assert.Equal(PaymentMethodTokenException.Invalid, exception.Code);
        }

        [Fact]
        public async Task Tokenize_DoesNotPersistRawCardNumberOrCvv()
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext, new TestTimeProvider(InitialTime));
            const string cardNumber = "4111111111111111";
            const string cvv = "987";

            await service.TokenizeAsync(new CreditCardInfo
            {
                CardNumber = cardNumber,
                ExpiryDate = "12/30",
                CVV = cvv,
                OwnerName = "Jane Doe"
            });

            var record = await dbContext.PaymentMethodTokens.SingleAsync();
            var persistedValues = string.Join(
                "|",
                record.TokenHash,
                record.MaskedCardNumber,
                record.OwnerName);
            Assert.DoesNotContain(cardNumber, persistedValues);
            Assert.DoesNotContain(cvv, persistedValues);
        }

        [Fact]
        public async Task Tokenize_SimulatedDeclineCard_PreservesOnlySimulationFlag()
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext, new TestTimeProvider(InitialTime));

            var token = await service.TokenizeAsync(
                ValidCard(WalletSimulationPaymentGateway.SimulatedDeclineCardNumber));
            var redeemed = await service.RedeemAsync(token.PaymentMethodToken);

            Assert.True(redeemed.SimulatesDecline);
            Assert.EndsWith("0002", redeemed.MaskedCardNumber);
        }

        [Fact]
        public async Task Cleanup_DeletesOnlyExpiredOrRedeemedTokensPastRetention()
        {
            await using var dbContext = NewInMemoryContext();
            var timeProvider = new TestTimeProvider(InitialTime);
            dbContext.PaymentMethodTokens.AddRange(
                new()
                {
                    TokenHash = "expired-old",
                    ExpiresAt = InitialTime.UtcDateTime.AddHours(-25)
                },
                new()
                {
                    TokenHash = "redeemed-old",
                    ExpiresAt = InitialTime.UtcDateTime.AddHours(1),
                    RedeemedAt = InitialTime.UtcDateTime.AddHours(-25)
                },
                new()
                {
                    TokenHash = "expired-recent",
                    ExpiresAt = InitialTime.UtcDateTime.AddHours(-1)
                },
                new()
                {
                    TokenHash = "active",
                    ExpiresAt = InitialTime.UtcDateTime.AddHours(1)
                });
            await dbContext.SaveChangesAsync();
            var cleanup = new PaymentMethodTokenCleanupService(
                dbContext,
                timeProvider,
                Options.Create(new PaymentMethodTokenOptions { RetentionSeconds = 86400 }));

            var deleted = await cleanup.DeleteRetainedTokensAsync();

            Assert.Equal(2, deleted);
            Assert.Equal(
                ["active", "expired-recent"],
                await dbContext.PaymentMethodTokens
                    .OrderBy(token => token.TokenHash)
                    .Select(token => token.TokenHash)
                    .ToListAsync());
        }

        private sealed class TestTimeProvider : TimeProvider
        {
            private DateTimeOffset _utcNow;

            public TestTimeProvider(DateTimeOffset utcNow)
            {
                _utcNow = utcNow;
            }

            public override DateTimeOffset GetUtcNow() => _utcNow;

            public void Advance(TimeSpan duration)
            {
                _utcNow = _utcNow.Add(duration);
            }
        }
    }
}
