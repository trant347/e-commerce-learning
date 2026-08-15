using Microsoft.EntityFrameworkCore;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class LedgerQueryServiceTests
    {
        private static readonly DateTimeOffset FirstPosting =
            new(2033, 1, 1, 12, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset SecondPosting =
            FirstPosting.AddDays(1);

        [Fact]
        public async Task BalanceQueries_ReturnProjectionCurrentAndHistoricalValues()
        {
            await using var dbContext = NewContext();
            var seeded = await SeedAsync(dbContext);
            var service = new LedgerQueryService(dbContext);

            var projected = await service.GetProjectedBalanceAsync(
                seeded.AccountId);
            var current = await service.GetJournalBalanceAsync(
                seeded.AccountId);
            var afterFirst = await service.GetHistoricalBalanceAsync(
                seeded.AccountId,
                FirstPosting);
            var afterSecond = await service.GetHistoricalBalanceAsync(
                seeded.AccountId,
                SecondPosting);

            Assert.Equal(80m, projected.Balance);
            Assert.Equal(3, projected.ProjectionVersion);
            Assert.Equal(seeded.LastEntryId, projected.LastJournalEntryId);
            Assert.Equal(80m, current);
            Assert.Equal(100m, afterFirst);
            Assert.Equal(80m, afterSecond);
        }

        [Fact]
        public async Task GetStatementAsync_ReturnsStableOrderedPages()
        {
            await using var dbContext = NewContext();
            var seeded = await SeedAsync(dbContext);
            var service = new LedgerQueryService(dbContext);

            var firstPage = await service.GetStatementAsync(
                seeded.AccountId,
                pageNumber: 1,
                pageSize: 2);
            var secondPage = await service.GetStatementAsync(
                seeded.AccountId,
                pageNumber: 2,
                pageSize: 2);

            Assert.True(firstPage.HasMore);
            Assert.Equal(2, firstPage.Items.Count);
            Assert.Equal(100m, firstPage.Items[0].SignedAmount);
            Assert.Equal(-30m, firstPage.Items[1].SignedAmount);
            Assert.False(secondPage.HasMore);
            var finalItem = Assert.Single(secondPage.Items);
            Assert.Equal(10m, finalItem.SignedAmount);
            Assert.Equal(seeded.LastEntryId, finalItem.JournalEntryId);
        }

        [Fact]
        public async Task GetJournalBalanceAsync_UnknownAccount_Throws()
        {
            await using var dbContext = NewContext();
            var service = new LedgerQueryService(dbContext);

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                service.GetJournalBalanceAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task Queries_TranslateOnPostgres()
        {
            var connectionString =
                Environment.GetEnvironmentVariable("PAYMENT_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using var dbContext = new PaymentDbContext(options);
            await dbContext.Database.MigrateAsync();
            var seeded = await SeedAsync(dbContext);
            var service = new LedgerQueryService(dbContext);

            var current = await service.GetJournalBalanceAsync(
                seeded.AccountId);
            var historical = await service.GetHistoricalBalanceAsync(
                seeded.AccountId,
                FirstPosting);
            var statement = await service.GetStatementAsync(
                seeded.AccountId,
                pageNumber: 1,
                pageSize: 2);

            Assert.Equal(80m, current);
            Assert.Equal(100m, historical);
            Assert.True(statement.HasMore);
            Assert.Equal(-30m, statement.Items[1].SignedAmount);
        }

        private static PaymentDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new PaymentDbContext(options);
        }

        private static async Task<SeededLedger> SeedAsync(
            PaymentDbContext dbContext)
        {
            var account = new LedgerAccount
            {
                OwnerUserId = "statement-user",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            var offset = new LedgerAccount
            {
                AccountType = LedgerAccount.TypeSystemIssuance,
                Currency = "USD"
            };
            var first = Entry(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                "statement-1",
                FirstPosting.UtcDateTime,
                account.Id,
                offset.Id,
                JournalLine.DirectionCredit,
                100m);
            var second = Entry(
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                "statement-2",
                SecondPosting.UtcDateTime,
                account.Id,
                offset.Id,
                JournalLine.DirectionDebit,
                30m);
            var third = Entry(
                Guid.Parse("10000000-0000-0000-0000-000000000003"),
                "statement-3",
                SecondPosting.UtcDateTime,
                account.Id,
                offset.Id,
                JournalLine.DirectionCredit,
                10m);
            dbContext.LedgerAccounts.AddRange(account, offset);
            dbContext.JournalEntries.AddRange(first, second, third);
            dbContext.Wallets.Add(new UserWallet
            {
                UserId = "statement-user",
                Balance = 80m,
                LedgerAccountId = account.Id,
                ProjectionVersion = 3,
                LastJournalEntryId = third.Id
            });
            await dbContext.SaveChangesAsync();
            return new SeededLedger(account.Id, third.Id);
        }

        private static JournalEntry Entry(
            Guid entryId,
            string idempotencyKey,
            DateTime createdAt,
            Guid accountId,
            Guid offsetAccountId,
            string accountDirection,
            decimal amount)
        {
            var entry = new JournalEntry
            {
                Id = entryId,
                IdempotencyKey = idempotencyKey,
                Operation = JournalEntry.OperationAdminAdjustment,
                Currency = "USD",
                Description = "Statement test",
                CreatedAt = createdAt
            };
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 1,
                AccountId = accountId,
                Direction = accountDirection,
                Amount = amount,
                CreatedAt = createdAt
            });
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 2,
                AccountId = offsetAccountId,
                Direction = accountDirection == JournalLine.DirectionCredit
                    ? JournalLine.DirectionDebit
                    : JournalLine.DirectionCredit,
                Amount = amount,
                CreatedAt = createdAt
            });
            return entry;
        }

        private sealed record SeededLedger(
            Guid AccountId,
            Guid LastEntryId);
    }
}
