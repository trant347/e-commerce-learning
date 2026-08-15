using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Contracts;
using payment_service.Data;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    /// <summary>
    /// Covers PAYMENT_SAGA_SPEC.md's migration step 3: a lookup a saga reconciliation job can
    /// use to check "did this charge actually happen?" for a given sagaId.
    /// </summary>
    public class PaymentServiceTransactionLookupTests
    {
        private static PaymentDbContext NewInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new PaymentDbContext(options);
        }

        private static PaymentRequest NewRequest(decimal amount, Guid? sagaId) => new()
        {
            CreditCard = new CreditCardInfo
            {
                CardNumber = "4111111111111111",
                ExpiryDate = "12/30",
                CVV = "123",
                OwnerName = "Jane Doe"
            },
            Amount = amount,
            Currency = "USD",
            SagaId = sagaId
        };

        private static PaymentService NewService(PaymentDbContext dbContext) =>
            new(
                dbContext,
                TestPaymentServices.CreateLegacyGateway(dbContext),
                NullLogger<PaymentService>.Instance);

        [Fact]
        public async Task GetTransactionBySagaIdAsync_KnownSagaId_ReturnsTransaction()
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext);
            var sagaId = Guid.NewGuid();
            var processed = await service.ProcessPaymentAsync(NewRequest(30m, sagaId));

            var found = await service.GetTransactionBySagaIdAsync(sagaId);

            Assert.NotNull(found);
            Assert.Equal(processed.Id, found!.Id);
        }

        [Fact]
        public async Task GetTransactionBySagaIdAsync_UnknownSagaId_ReturnsNull()
        {
            await using var dbContext = NewInMemoryContext();
            var service = NewService(dbContext);

            var found = await service.GetTransactionBySagaIdAsync(Guid.NewGuid());

            Assert.Null(found);
        }
    }
}
