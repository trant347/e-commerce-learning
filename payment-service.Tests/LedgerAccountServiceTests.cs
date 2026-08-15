using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    public class LedgerAccountServiceTests
    {
        [Fact]
        public async Task EnsureUserWalletAccountAsync_NewUser_PostsStartingBalanceOnce()
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);

            var first = await service.EnsureUserWalletAccountAsync("alice");
            var second = await service.EnsureUserWalletAccountAsync("alice");

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(2, await dbContext.LedgerAccounts.CountAsync());
            var entry = await dbContext.JournalEntries
                .Include(candidate => candidate.Lines)
                .SingleAsync();
            Assert.Equal(
                JournalEntry.OperationUserRegistrationCredit,
                entry.Operation);
            Assert.Equal(2, entry.Lines.Count);
            Assert.Equal(
                entry.Lines.Where(line => line.Direction == JournalLine.DirectionDebit)
                    .Sum(line => line.Amount),
                entry.Lines.Where(line => line.Direction == JournalLine.DirectionCredit)
                    .Sum(line => line.Amount));
            var wallet = await dbContext.Wallets.SingleAsync();
            Assert.Equal(first.Id, wallet.LedgerAccountId);
            Assert.Equal(entry.Id, wallet.LastJournalEntryId);
            Assert.Equal(1, wallet.ProjectionVersion);
            Assert.Equal(UserWallet.DefaultStartingBalance, wallet.Balance);
        }

        [Fact]
        public async Task EnsureCustodyAccountAsync_CreatesZeroBalanceWithoutIssuance()
        {
            await using var dbContext = NewContext();
            var service = NewService(dbContext);

            var account = await service.EnsureCustodyAccountAsync("custody");

            Assert.Equal(LedgerAccount.TypeEscrowCustody, account.AccountType);
            Assert.Equal(0, await dbContext.JournalEntries.CountAsync());
            var wallet = await dbContext.Wallets.SingleAsync();
            Assert.Equal(0m, wallet.Balance);
            Assert.Equal(account.Id, wallet.LedgerAccountId);
        }

        [Fact]
        public async Task EnsureUserWalletAccountAsync_LegacyWallet_DoesNotInventRegistrationEntry()
        {
            await using var dbContext = NewContext();
            dbContext.Wallets.Add(new UserWallet
            {
                UserId = "legacy-user",
                Balance = 250m
            });
            await dbContext.SaveChangesAsync();
            var service = NewService(dbContext);

            var account = await service.EnsureUserWalletAccountAsync("legacy-user");

            Assert.Equal(0, await dbContext.JournalEntries.CountAsync());
            var wallet = await dbContext.Wallets.SingleAsync();
            Assert.Equal(account.Id, wallet.LedgerAccountId);
            Assert.Equal(250m, wallet.Balance);
            Assert.Equal(0, wallet.ProjectionVersion);
        }

        private static PaymentDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new PaymentDbContext(options);
        }

        private static LedgerAccountService NewService(PaymentDbContext dbContext) =>
            new(
                dbContext,
                TimeProvider.System,
                NullLogger<LedgerAccountService>.Instance);
    }
}
