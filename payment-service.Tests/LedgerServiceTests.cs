using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class LedgerServiceTests
    {
        private static readonly DateTimeOffset PostedAt =
            new(2032, 4, 5, 6, 7, 8, TimeSpan.Zero);

        [Fact]
        public async Task PostTransferAsync_NewTransfer_CreatesEntryAndUpdatesProjections()
        {
            await using var dbContext = NewContext();
            var accounts = await SeedAccountsAsync(dbContext);
            var service = NewService(dbContext);

            var result = await service.PostTransferAsync(NewTransfer(30m));

            Assert.False(result.WasAlreadyPosted);
            Assert.Equal(1, await dbContext.JournalEntries.CountAsync());
            var entry = await dbContext.JournalEntries
                .Include(candidate => candidate.Lines)
                .SingleAsync();
            Assert.Equal(PostedAt.UtcDateTime, entry.CreatedAt);
            Assert.Equal(2, entry.Lines.Count);
            Assert.Contains(entry.Lines, line =>
                line.AccountId == accounts.PayerAccountId
                && line.Direction == JournalLine.DirectionDebit
                && line.Amount == 30m);
            Assert.Contains(entry.Lines, line =>
                line.AccountId == accounts.PayeeAccountId
                && line.Direction == JournalLine.DirectionCredit
                && line.Amount == 30m);
            var payer = await dbContext.Wallets.SingleAsync(
                wallet => wallet.UserId == "payer");
            var payee = await dbContext.Wallets.SingleAsync(
                wallet => wallet.UserId == "payee");
            Assert.Equal(70m, payer.Balance);
            Assert.Equal(50m, payee.Balance);
            Assert.Equal(1, payer.ProjectionVersion);
            Assert.Equal(1, payee.ProjectionVersion);
            Assert.Equal(entry.Id, payer.LastJournalEntryId);
            Assert.Equal(entry.Id, payee.LastJournalEntryId);
        }

        [Fact]
        public async Task PostTransferAsync_DuplicateTerms_ReturnsOriginalWithoutMovingAgain()
        {
            await using var dbContext = NewContext();
            await SeedAccountsAsync(dbContext);
            var service = NewService(dbContext);
            var transfer = NewTransfer(30m);

            var first = await service.PostTransferAsync(transfer);
            var second = await service.PostTransferAsync(transfer);

            Assert.False(first.WasAlreadyPosted);
            Assert.True(second.WasAlreadyPosted);
            Assert.Equal(first.JournalEntry.Id, second.JournalEntry.Id);
            Assert.Equal(1, await dbContext.JournalEntries.CountAsync());
            Assert.Equal(
                70m,
                (await dbContext.Wallets.SingleAsync(
                    wallet => wallet.UserId == "payer")).Balance);
            Assert.Equal(
                1,
                (await dbContext.Wallets.SingleAsync(
                    wallet => wallet.UserId == "payer")).ProjectionVersion);
        }

        [Fact]
        public async Task PostTransferAsync_ConflictingIdempotencyKey_IsRejected()
        {
            await using var dbContext = NewContext();
            await SeedAccountsAsync(dbContext);
            var service = NewService(dbContext);
            await service.PostTransferAsync(NewTransfer(30m));

            await Assert.ThrowsAsync<LedgerPostingConflictException>(() =>
                service.PostTransferAsync(NewTransfer(31m)));

            Assert.Equal(1, await dbContext.JournalEntries.CountAsync());
            Assert.Equal(
                70m,
                (await dbContext.Wallets.SingleAsync(
                    wallet => wallet.UserId == "payer")).Balance);
        }

        [Fact]
        public async Task PostTransferAsync_InsufficientFunds_DoesNotPost()
        {
            await using var dbContext = NewContext();
            await SeedAccountsAsync(dbContext);
            var service = NewService(dbContext);

            var exception = await Assert.ThrowsAsync<InsufficientLedgerFundsException>(
                () => service.PostTransferAsync(NewTransfer(101m)));

            Assert.Equal(100m, exception.AvailableBalance);
            Assert.Equal(0, await dbContext.JournalEntries.CountAsync());
            Assert.Equal(
                100m,
                (await dbContext.Wallets.SingleAsync(
                    wallet => wallet.UserId == "payer")).Balance);
        }

        private static PaymentDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new PaymentDbContext(options);
        }

        private static LedgerService NewService(PaymentDbContext dbContext) =>
            new(
                dbContext,
                new FixedTimeProvider(PostedAt),
                NullLogger<LedgerService>.Instance);

        private static LedgerTransfer NewTransfer(decimal amount) => new()
        {
            IdempotencyKey = "test-transfer",
            Operation = JournalEntry.OperationLegacyPayment,
            Currency = "USD",
            Amount = amount,
            DebitAccount = new LedgerAccountReference(
                "payer",
                LedgerAccount.TypeUserWallet),
            CreditAccount = new LedgerAccountReference(
                "payee",
                LedgerAccount.TypeUserWallet),
            Description = "Test wallet transfer"
        };

        private static async Task<SeededAccounts> SeedAccountsAsync(
            PaymentDbContext dbContext)
        {
            var payerAccount = new LedgerAccount
            {
                OwnerUserId = "payer",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            var payeeAccount = new LedgerAccount
            {
                OwnerUserId = "payee",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            dbContext.LedgerAccounts.AddRange(payerAccount, payeeAccount);
            dbContext.Wallets.AddRange(
                new UserWallet
                {
                    UserId = "payer",
                    Balance = 100m,
                    LedgerAccountId = payerAccount.Id
                },
                new UserWallet
                {
                    UserId = "payee",
                    Balance = 20m,
                    LedgerAccountId = payeeAccount.Id
                });
            await dbContext.SaveChangesAsync();
            return new SeededAccounts(payerAccount.Id, payeeAccount.Id);
        }

        private sealed record SeededAccounts(
            Guid PayerAccountId,
            Guid PayeeAccountId);

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
