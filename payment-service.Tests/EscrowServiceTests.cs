using Microsoft.EntityFrameworkCore;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class EscrowServiceTests
    {
        private static readonly DateTimeOffset Now =
            new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task CreateAsync_CreatesPendingEscrowWithImmutableBookingTerms()
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);
            var escrowId = Guid.NewGuid();

            var escrow = await service.CreateAsync(
                escrowId,
                "booking-1",
                125.555m,
                "usd",
                "requester",
                "taskmaster",
                "admin-custody");

            Assert.Equal(escrowId, escrow.Id);
            Assert.Equal("booking-1", escrow.BookingId);
            Assert.Equal(125.56m, escrow.Amount);
            Assert.Equal("USD", escrow.Currency);
            Assert.Equal("requester", escrow.RequesterUserId);
            Assert.Equal("taskmaster", escrow.TaskMasterUserId);
            Assert.Equal("admin-custody", escrow.CustodyUserId);
            Assert.Equal(EscrowRecord.StatusPending, escrow.Status);
            Assert.Equal(Now.UtcDateTime, escrow.CreatedAt);
        }

        [Fact]
        public async Task CreateAsync_SameBookingAndTerms_ReturnsExistingEscrow()
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);
            var escrowId = Guid.NewGuid();

            var first = await CreateAsync(service, escrowId);
            var second = await CreateAsync(service, escrowId);

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(1, await dbContext.Escrows.CountAsync());
        }

        [Fact]
        public async Task CreateAsync_SameBookingWithDifferentTerms_Throws()
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);
            await CreateAsync(service, Guid.NewGuid());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateAsync(
                    Guid.NewGuid(),
                    "booking-1",
                    999m,
                    "USD",
                    "requester",
                    "taskmaster",
                    "admin-custody"));

            Assert.Contains("different immutable details", exception.Message);
        }

        [Fact]
        public async Task Lifecycle_PendingToFundedToReleased_PersistsTransactionIds()
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);
            var escrowId = Guid.NewGuid();
            var fundingTransactionId = Guid.NewGuid();
            var releaseTransactionId = Guid.NewGuid();
            await CreateAsync(service, escrowId);

            var funded = await service.MarkFundedAsync(escrowId, fundingTransactionId);
            var released = await service.MarkReleasedAsync(escrowId, releaseTransactionId);

            Assert.Equal(EscrowRecord.StatusFunded, funded.Status);
            Assert.Equal(fundingTransactionId, funded.FundingTransactionId);
            Assert.Equal(EscrowRecord.StatusReleased, released.Status);
            Assert.Equal(fundingTransactionId, released.FundingTransactionId);
            Assert.Equal(releaseTransactionId, released.ReleaseTransactionId);
            Assert.Null(released.RefundTransactionId);
        }

        [Fact]
        public async Task Lifecycle_PendingToFundedToRefunded_PersistsTransactionIds()
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);
            var escrowId = Guid.NewGuid();
            var fundingTransactionId = Guid.NewGuid();
            var refundTransactionId = Guid.NewGuid();
            await CreateAsync(service, escrowId);

            await service.MarkFundedAsync(escrowId, fundingTransactionId);
            var refunded = await service.MarkRefundedAsync(escrowId, refundTransactionId);

            Assert.Equal(EscrowRecord.StatusRefunded, refunded.Status);
            Assert.Equal(fundingTransactionId, refunded.FundingTransactionId);
            Assert.Equal(refundTransactionId, refunded.RefundTransactionId);
            Assert.Null(refunded.ReleaseTransactionId);
        }

        [Theory]
        [InlineData("release")]
        [InlineData("refund")]
        public async Task PendingEscrow_CannotReleaseOrRefund(string operation)
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);
            var escrowId = Guid.NewGuid();
            await CreateAsync(service, escrowId);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                operation == "release"
                    ? service.MarkReleasedAsync(escrowId, Guid.NewGuid())
                    : service.MarkRefundedAsync(escrowId, Guid.NewGuid()));

            Assert.Contains(EscrowRecord.StatusPending, exception.Message);
        }

        [Theory]
        [InlineData("fund")]
        [InlineData("release")]
        [InlineData("refund")]
        public async Task ReleasedEscrow_RejectsEveryDuplicateOrTerminalTransition(string operation)
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);
            var escrowId = Guid.NewGuid();
            await CreateAsync(service, escrowId);
            await service.MarkFundedAsync(escrowId, Guid.NewGuid());
            await service.MarkReleasedAsync(escrowId, Guid.NewGuid());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TransitionAsync(service, escrowId, operation));
        }

        [Theory]
        [InlineData("fund")]
        [InlineData("release")]
        [InlineData("refund")]
        public async Task RefundedEscrow_RejectsEveryDuplicateOrTerminalTransition(string operation)
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);
            var escrowId = Guid.NewGuid();
            await CreateAsync(service, escrowId);
            await service.MarkFundedAsync(escrowId, Guid.NewGuid());
            await service.MarkRefundedAsync(escrowId, Guid.NewGuid());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TransitionAsync(service, escrowId, operation));
        }

        [Fact]
        public async Task CompetingReleaseAndRefund_OnlyFirstTerminalTransitionSucceeds()
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);
            var escrowId = Guid.NewGuid();
            await CreateAsync(service, escrowId);
            await service.MarkFundedAsync(escrowId, Guid.NewGuid());

            await service.MarkReleasedAsync(escrowId, Guid.NewGuid());
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.MarkRefundedAsync(escrowId, Guid.NewGuid()));

            var escrow = await service.GetByIdAsync(escrowId);
            Assert.Equal(EscrowRecord.StatusReleased, escrow!.Status);
            Assert.NotNull(escrow.ReleaseTransactionId);
            Assert.Null(escrow.RefundTransactionId);
        }

        [Fact]
        public async Task UnknownEscrow_TransitionThrowsNotFound()
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);

            await Assert.ThrowsAsync<KeyNotFoundException>(
                () => service.MarkFundedAsync(Guid.NewGuid(), Guid.NewGuid()));
        }

        private static Task<EscrowRecord> CreateAsync(
            EscrowService service,
            Guid escrowId) =>
            service.CreateAsync(
                escrowId,
                "booking-1",
                100m,
                "USD",
                "requester",
                "taskmaster",
                "admin-custody");

        private static Task<EscrowRecord> TransitionAsync(
            EscrowService service,
            Guid escrowId,
            string operation) =>
            operation switch
            {
                "fund" => service.MarkFundedAsync(escrowId, Guid.NewGuid()),
                "release" => service.MarkReleasedAsync(escrowId, Guid.NewGuid()),
                "refund" => service.MarkRefundedAsync(escrowId, Guid.NewGuid()),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };

        private static PaymentDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new PaymentDbContext(options);
        }

        private static EscrowService NewService(PaymentDbContext dbContext) =>
            new(dbContext, new FixedTimeProvider(Now));

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
