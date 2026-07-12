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
    /// Covers the sagaId idempotency contract added for PAYMENT_SAGA_SPEC.md's migration step 2:
    /// a retried request carrying the same SagaId must not double-charge.
    /// </summary>
    public class PaymentServiceSagaIdTests
    {
        private static PaymentDbContext NewInMemoryContext()
        {
            // The in-memory provider doesn't support real transactions; PaymentService wraps
            // its write in one for the real (Postgres) provider, so silence that warning here
            // rather than failing the test on provider-specific behavior we don't care about.
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
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

        [Fact]
        public async Task ProcessPaymentAsync_WithNewSagaId_CreatesTransactionAndPersistsSagaId()
        {
            await using var dbContext = NewInMemoryContext();
            var service = new PaymentService(dbContext, NullLogger<PaymentService>.Instance);
            var sagaId = Guid.NewGuid();

            var transaction = await service.ProcessPaymentAsync(NewRequest(50m, sagaId));

            Assert.Equal(sagaId, transaction.SagaId);
            Assert.Equal(1, await dbContext.Transactions.CountAsync());
        }

        [Fact]
        public async Task ProcessPaymentAsync_RetriedWithSameSagaId_DedupesInsteadOfDoubleCharging()
        {
            await using var dbContext = NewInMemoryContext();
            var service = new PaymentService(dbContext, NullLogger<PaymentService>.Instance);
            var sagaId = Guid.NewGuid();

            var first = await service.ProcessPaymentAsync(NewRequest(75m, sagaId));
            var retry = await service.ProcessPaymentAsync(NewRequest(75m, sagaId));

            Assert.Equal(first.Id, retry.Id);
            Assert.Equal(1, await dbContext.Transactions.CountAsync());
        }

        [Fact]
        public async Task ProcessPaymentAsync_WithoutSagaId_AlwaysCreatesNewTransaction()
        {
            await using var dbContext = NewInMemoryContext();
            var service = new PaymentService(dbContext, NullLogger<PaymentService>.Instance);

            var first = await service.ProcessPaymentAsync(NewRequest(20m, null));
            var second = await service.ProcessPaymentAsync(NewRequest(20m, null));

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, await dbContext.Transactions.CountAsync());
        }
    }
}
