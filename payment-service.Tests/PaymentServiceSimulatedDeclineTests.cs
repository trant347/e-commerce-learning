using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Contracts;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    /// <summary>
    /// Covers the "magic" test card number that deterministically simulates a declined charge
    /// (see PaymentService.SimulatedDeclineCardNumber / PAYMENT_SAGA_SPEC.md), added so the
    /// booking-payment saga's decline handling can be exercised via a normal HTTP request
    /// instead of a debugger.
    /// </summary>
    public class PaymentServiceSimulatedDeclineTests
    {
        private static PaymentDbContext NewInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new PaymentDbContext(options);
        }

        private static PaymentRequest NewRequest(string cardNumber, Guid? sagaId = null) => new()
        {
            CreditCard = new CreditCardInfo
            {
                CardNumber = cardNumber,
                ExpiryDate = "12/30",
                CVV = "123",
                OwnerName = "Jane Doe"
            },
            Amount = 100m,
            Currency = "USD",
            SagaId = sagaId
        };

        private static PaymentService NewService(PaymentDbContext dbContext) =>
            new(dbContext, new WalletSimulationPaymentGateway(dbContext, NullLogger<WalletSimulationPaymentGateway>.Instance), NullLogger<PaymentService>.Instance);

        [Fact]
        public async Task ProcessPaymentAsync_WithSimulatedDeclineCard_ReturnsDeclinedTransaction()
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext);

            var transaction = await service.ProcessPaymentAsync(NewRequest(PaymentService.SimulatedDeclineCardNumber));

            Assert.Equal(PaymentTransaction.StatusDeclined, transaction.Status);
            // A declined attempt is still persisted, mirroring how a real gateway would log it.
            Assert.Equal(1, await dbContext.Transactions.CountAsync());
        }

        [Fact]
        public async Task ProcessPaymentAsync_WithOrdinaryCard_ReturnsApprovedTransaction()
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext);

            var transaction = await service.ProcessPaymentAsync(NewRequest("4111111111111111"));

            Assert.Equal(PaymentTransaction.StatusApproved, transaction.Status);
        }
    }
}
