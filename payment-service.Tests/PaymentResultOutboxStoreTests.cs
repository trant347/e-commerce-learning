using Microsoft.EntityFrameworkCore;
using payment_service.Data;
using payment_service.MessageQueue;
using payment_service.Models;
using Xunit;

namespace payment_service.Tests
{
    public class PaymentResultOutboxStoreTests
    {
        private static readonly DateTimeOffset Now =
            new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task ReconcileMissingAsync_AddsOneOutboxRowPerSaga()
        {
            await using var dbContext = NewContext();
            var transaction = EscrowTransaction();
            dbContext.Transactions.Add(transaction);
            await dbContext.SaveChangesAsync();
            var store = new PaymentResultOutboxStore(
                dbContext,
                new FixedTimeProvider(Now));

            var firstCount = await store.ReconcileMissingAsync(
                CancellationToken.None);
            var secondCount = await store.ReconcileMissingAsync(
                CancellationToken.None);

            Assert.Equal(1, firstCount);
            Assert.Equal(0, secondCount);
            var row = await dbContext.PaymentResultOutbox.SingleAsync();
            Assert.Equal(transaction.SagaId, row.SagaId);
            Assert.Equal(transaction.Id, row.TransactionId);
        }

        [Fact]
        public async Task TryClaimNextAsync_ExpiredLease_ReclaimsForRestart()
        {
            await using var dbContext = NewContext();
            var row = new PaymentResultOutbox
            {
                SagaId = Guid.NewGuid(),
                TransactionId = Guid.NewGuid(),
                Payload = "{}",
                DispatchStatus = PaymentResultOutbox.StatusClaimed,
                DispatchAttemptCount = 1,
                DispatchClaimedAt = Now.UtcDateTime.AddMinutes(-2),
                DispatchClaimExpiresAt = Now.UtcDateTime.AddMinutes(-1),
                NextDispatchAttemptAt = Now.UtcDateTime.AddMinutes(-2),
                CreatedAt = Now.UtcDateTime.AddMinutes(-3)
            };
            dbContext.PaymentResultOutbox.Add(row);
            await dbContext.SaveChangesAsync();
            var store = new PaymentResultOutboxStore(
                dbContext,
                new FixedTimeProvider(Now));

            var claimed = await store.TryClaimNextAsync(
                TimeSpan.FromSeconds(60),
                CancellationToken.None);

            Assert.Same(row, claimed);
            Assert.Equal(2, row.DispatchAttemptCount);
            Assert.Equal(Now.UtcDateTime, row.DispatchClaimedAt);
            Assert.Equal(
                Now.UtcDateTime.AddSeconds(60),
                row.DispatchClaimExpiresAt);
        }

        private static PaymentDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new PaymentDbContext(options);
        }

        private static PaymentTransaction EscrowTransaction() => new()
        {
            Amount = 100m,
            Currency = "USD",
            MaskedCardNumber = "ESCROW",
            OwnerName = "requester",
            Status = PaymentTransaction.StatusApproved,
            SagaId = Guid.NewGuid(),
            EscrowId = Guid.NewGuid(),
            BookingId = "booking-1",
            Operation = "FUND_ESCROW",
            PayerUserId = "requester",
            PayeeUserId = "admin-custody",
            TaskMasterUserId = "taskmaster",
            CreatedAt = Now.UtcDateTime
        };

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _utcNow;

            public FixedTimeProvider(DateTimeOffset utcNow)
            {
                _utcNow = utcNow;
            }

            public override DateTimeOffset GetUtcNow() => _utcNow;
        }
    }
}
