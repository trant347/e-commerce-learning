using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Payment.Contracts.V1;
using Xunit;

namespace payment_service.Tests
{
    public class PaymentRequestProcessorTests
    {
        private static readonly DateTimeOffset Now =
            new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task FundEscrow_ValidToken_MovesRequesterFundsToCustody()
        {
            await using var dbContext = NewContext();
            SeedWallets(
                dbContext,
                ("requester", 500m),
                ("admin-custody", 100m),
                ("taskmaster", 0m));
            var tokenService = ValidTokenService();
            var processor = NewProcessor(dbContext, tokenService.Object);
            var request = FundingRequest();

            var result = await processor.ProcessAsync(request);

            Assert.Equal(PaymentResultV1.StatusApproved, result.Status);
            Assert.Equal(400m, WalletBalance(dbContext, "requester"));
            Assert.Equal(200m, WalletBalance(dbContext, "admin-custody"));
            var escrow = await dbContext.Escrows.SingleAsync();
            Assert.Equal(EscrowRecord.StatusFunded, escrow.Status);
            Assert.Equal(result.TransactionId, escrow.FundingTransactionId);
            var transaction = await dbContext.Transactions.SingleAsync();
            Assert.Equal(request.SagaId, transaction.SagaId);
            Assert.Equal(request.EscrowId, transaction.EscrowId);
            Assert.Equal(PaymentOperation.FundEscrow, transaction.Operation);
            var journal = await dbContext.JournalEntries
                .Include(entry => entry.Lines)
                .SingleAsync();
            Assert.Equal(transaction.Id, journal.PaymentTransactionId);
            Assert.Equal(PaymentOperation.FundEscrow, journal.Operation);
            Assert.Equal(2, journal.Lines.Count);
            var outbox = await dbContext.PaymentResultOutbox.SingleAsync();
            Assert.Equal(request.SagaId, outbox.SagaId);
            Assert.Equal(result.TransactionId, outbox.TransactionId);
        }

        [Fact]
        public async Task ReleaseEscrow_FundedEscrow_MovesCustodyFundsToTaskMaster()
        {
            await using var dbContext = NewContext();
            var request = ReleaseRequest();
            SeedWallets(
                dbContext,
                ("admin-custody", 300m),
                ("taskmaster", 50m),
                ("requester", 400m));
            SeedEscrow(dbContext, request, EscrowRecord.StatusFunded);
            var processor = NewProcessor(
                dbContext,
                new Mock<IPaymentMethodTokenService>().Object);

            var result = await processor.ProcessAsync(request);

            Assert.Equal(PaymentResultV1.StatusApproved, result.Status);
            Assert.Equal(200m, WalletBalance(dbContext, "admin-custody"));
            Assert.Equal(150m, WalletBalance(dbContext, "taskmaster"));
            var escrow = await dbContext.Escrows.SingleAsync();
            Assert.Equal(EscrowRecord.StatusReleased, escrow.Status);
            Assert.Equal(result.TransactionId, escrow.ReleaseTransactionId);
            Assert.Equal(
                PaymentOperation.ReleaseEscrow,
                (await dbContext.JournalEntries.SingleAsync()).Operation);
        }

        [Fact]
        public async Task RefundEscrow_FundedEscrow_ReturnsCustodyFundsToRequester()
        {
            await using var dbContext = NewContext();
            var request = RefundRequest();
            SeedWallets(
                dbContext,
                ("admin-custody", 300m),
                ("requester", 25m),
                ("taskmaster", 50m));
            SeedEscrow(dbContext, request, EscrowRecord.StatusFunded);
            var processor = NewProcessor(
                dbContext,
                new Mock<IPaymentMethodTokenService>().Object);

            var result = await processor.ProcessAsync(request);

            Assert.Equal(PaymentResultV1.StatusApproved, result.Status);
            Assert.Equal(200m, WalletBalance(dbContext, "admin-custody"));
            Assert.Equal(125m, WalletBalance(dbContext, "requester"));
            var escrow = await dbContext.Escrows.SingleAsync();
            Assert.Equal(EscrowRecord.StatusRefunded, escrow.Status);
            Assert.Equal(result.TransactionId, escrow.RefundTransactionId);
            Assert.Equal(
                PaymentOperation.RefundEscrow,
                (await dbContext.JournalEntries.SingleAsync()).Operation);
        }

        [Fact]
        public async Task FundEscrow_InsufficientBalance_PersistsDeclineWithoutMovement()
        {
            await using var dbContext = NewContext();
            SeedWallets(
                dbContext,
                ("requester", 50m),
                ("admin-custody", 100m),
                ("taskmaster", 0m));
            var tokenService = ValidTokenService();
            var processor = NewProcessor(dbContext, tokenService.Object);
            var request = FundingRequest();

            var result = await processor.ProcessAsync(request);

            Assert.Equal(PaymentResultV1.StatusDeclined, result.Status);
            Assert.Contains(
                "Insufficient balance",
                result.DeclineReason,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(50m, WalletBalance(dbContext, "requester"));
            Assert.Equal(100m, WalletBalance(dbContext, "admin-custody"));
            Assert.Equal(
                EscrowRecord.StatusPending,
                (await dbContext.Escrows.SingleAsync()).Status);
            Assert.Single(dbContext.Transactions);
            Assert.Single(dbContext.PaymentResultOutbox);
            Assert.Empty(dbContext.JournalEntries);
        }

        [Fact]
        public async Task ReleaseEscrow_WhenEscrowIsPending_ThrowsWithoutMovement()
        {
            await using var dbContext = NewContext();
            var request = ReleaseRequest();
            SeedWallets(
                dbContext,
                ("admin-custody", 300m),
                ("taskmaster", 50m),
                ("requester", 400m));
            SeedEscrow(dbContext, request, EscrowRecord.StatusPending);
            var processor = NewProcessor(
                dbContext,
                new Mock<IPaymentMethodTokenService>().Object);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => processor.ProcessAsync(request));

            Assert.Contains(EscrowRecord.StatusFunded, exception.Message);
            Assert.Equal(300m, WalletBalance(dbContext, "admin-custody"));
            Assert.Equal(50m, WalletBalance(dbContext, "taskmaster"));
            Assert.Empty(dbContext.Transactions);
        }

        [Fact]
        public async Task DuplicateSaga_ReturnsOriginalResultWithoutMovingMoneyAgain()
        {
            await using var dbContext = NewContext();
            SeedWallets(
                dbContext,
                ("requester", 500m),
                ("admin-custody", 100m),
                ("taskmaster", 0m));
            var tokenService = ValidTokenService();
            var processor = NewProcessor(dbContext, tokenService.Object);
            var request = FundingRequest();

            var first = await processor.ProcessAsync(request);
            var duplicate = await processor.ProcessAsync(request);

            Assert.Equal(first, duplicate);
            Assert.Equal(400m, WalletBalance(dbContext, "requester"));
            Assert.Equal(200m, WalletBalance(dbContext, "admin-custody"));
            Assert.Single(dbContext.Transactions);
            Assert.Single(dbContext.PaymentResultOutbox);
            Assert.Single(dbContext.JournalEntries);
            tokenService.Verify(
                service => service.RedeemAsync(
                    request.PaymentMethodToken!,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task UnsupportedSchemaVersion_IsRejectedBeforeTokenRedemption()
        {
            await using var dbContext = NewContext();
            var tokenService = ValidTokenService();
            var processor = NewProcessor(dbContext, tokenService.Object);
            var request = FundingRequest() with { SchemaVersion = 99 };

            await Assert.ThrowsAsync<ArgumentException>(
                () => processor.ProcessAsync(request));

            tokenService.Verify(
                service => service.RedeemAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            Assert.Empty(dbContext.Transactions);
        }

        [Fact]
        public async Task ResultOutboxWriteFailure_RollsBackPaymentChanges()
        {
            await using var connection =
                new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var interceptor = new FailOutboxSaveInterceptor();
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;
            await using (var setup = new PaymentDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                SeedWallets(
                    setup,
                    ("requester", 500m),
                    ("admin-custody", 100m),
                    ("taskmaster", 0m));
            }

            interceptor.FailOutboxWrites = true;
            await using (var processing = new PaymentDbContext(options))
            {
                var processor = NewProcessor(
                    processing,
                    ValidTokenService().Object);

                await Assert.ThrowsAsync<DbUpdateException>(
                    () => processor.ProcessAsync(FundingRequest()));
            }

            await using var verification = new PaymentDbContext(options);
            Assert.Equal(500m, WalletBalance(verification, "requester"));
            Assert.Equal(
                100m,
                WalletBalance(verification, "admin-custody"));
            Assert.Empty(verification.Escrows);
            Assert.Empty(verification.Transactions);
            Assert.Empty(verification.PaymentResultOutbox);
            Assert.Empty(verification.JournalEntries);
        }

        private static PaymentDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new PaymentDbContext(options);
        }

        private static PaymentRequestProcessor NewProcessor(
            PaymentDbContext dbContext,
            IPaymentMethodTokenService tokenService)
        {
            var timeProvider = new FixedTimeProvider(Now);
            var ledger = new LedgerService(
                dbContext,
                timeProvider,
                NullLogger<LedgerService>.Instance);
            return new PaymentRequestProcessor(
                dbContext,
                tokenService,
                ledger,
                timeProvider,
                NullLogger<PaymentRequestProcessor>.Instance);
        }

        private static Mock<IPaymentMethodTokenService> ValidTokenService()
        {
            var tokenService = new Mock<IPaymentMethodTokenService>();
            tokenService.Setup(service => service.RedeemAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RedeemedPaymentMethod(
                    "************1111",
                    "Requester",
                    false));
            return tokenService;
        }

        private static PaymentRequestedV1 FundingRequest() => new()
        {
            SagaId = Guid.NewGuid(),
            EscrowId = Guid.NewGuid(),
            BookingId = "booking-1",
            Operation = PaymentOperation.FundEscrow,
            Amount = 100m,
            Currency = "USD",
            PayerUserId = "requester",
            PayeeUserId = "admin-custody",
            TaskMasterUserId = "taskmaster",
            PaymentMethodToken = "pmt_token"
        };

        private static PaymentRequestedV1 ReleaseRequest() => new()
        {
            SagaId = Guid.NewGuid(),
            EscrowId = Guid.NewGuid(),
            BookingId = "booking-1",
            Operation = PaymentOperation.ReleaseEscrow,
            Amount = 100m,
            Currency = "USD",
            PayerUserId = "admin-custody",
            PayeeUserId = "taskmaster",
            TaskMasterUserId = "taskmaster"
        };

        private static PaymentRequestedV1 RefundRequest() => new()
        {
            SagaId = Guid.NewGuid(),
            EscrowId = Guid.NewGuid(),
            BookingId = "booking-1",
            Operation = PaymentOperation.RefundEscrow,
            Amount = 100m,
            Currency = "USD",
            PayerUserId = "admin-custody",
            PayeeUserId = "requester",
            TaskMasterUserId = "taskmaster"
        };

        private static void SeedWallets(
            PaymentDbContext dbContext,
            params (string UserId, decimal Balance)[] wallets)
        {
            foreach (var wallet in wallets)
            {
                var account = new LedgerAccount
                {
                    OwnerUserId = wallet.UserId,
                    AccountType = wallet.UserId == "admin-custody"
                        ? LedgerAccount.TypeEscrowCustody
                        : LedgerAccount.TypeUserWallet,
                    Currency = "USD",
                    CreatedAt = Now.UtcDateTime
                };
                dbContext.LedgerAccounts.Add(account);
                dbContext.Wallets.Add(new UserWallet
                {
                    UserId = wallet.UserId,
                    Balance = wallet.Balance,
                    LedgerAccountId = account.Id,
                    CreatedAt = Now.UtcDateTime,
                    UpdatedAt = Now.UtcDateTime
                });
            }
            dbContext.SaveChanges();
        }

        private static void SeedEscrow(
            PaymentDbContext dbContext,
            PaymentRequestedV1 request,
            string status)
        {
            dbContext.Escrows.Add(new EscrowRecord
            {
                Id = request.EscrowId,
                BookingId = request.BookingId,
                Amount = request.Amount,
                Currency = request.Currency,
                RequesterUserId = "requester",
                TaskMasterUserId = request.TaskMasterUserId,
                CustodyUserId = "admin-custody",
                Status = status,
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
                FundedAt = status == EscrowRecord.StatusFunded
                    ? Now.UtcDateTime
                    : null,
                FundingTransactionId = status == EscrowRecord.StatusFunded
                    ? Guid.NewGuid()
                    : null
            });
            dbContext.SaveChanges();
        }

        private static decimal WalletBalance(
            PaymentDbContext dbContext,
            string userId) =>
            dbContext.Wallets.Single(wallet => wallet.UserId == userId).Balance;

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _utcNow;

            public FixedTimeProvider(DateTimeOffset utcNow)
            {
                _utcNow = utcNow;
            }

            public override DateTimeOffset GetUtcNow() => _utcNow;
        }

        private sealed class FailOutboxSaveInterceptor
            : SaveChangesInterceptor
        {
            public bool FailOutboxWrites { get; set; }

            public override ValueTask<int> SavedChangesAsync(
                    SaveChangesCompletedEventData eventData,
                    int result,
                    CancellationToken cancellationToken = default)
            {
                if (FailOutboxWrites)
                {
                    throw new DbUpdateException(
                        "Simulated outbox persistence failure.");
                }

                return base.SavedChangesAsync(
                    eventData,
                    result,
                    cancellationToken);
            }
        }
    }
}
