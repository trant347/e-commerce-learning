using Microsoft.EntityFrameworkCore;
using Npgsql;
using payment_service.Data;
using payment_service.Models;
using Xunit;

namespace payment_service.Tests
{
    public class WalletProjectionProtectionPostgresTests
    {
        [Fact]
        public async Task DirectBalanceUpdate_IsRejected()
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
            var account = new LedgerAccount
            {
                OwnerUserId = "protected-wallet",
                AccountType = LedgerAccount.TypeUserWallet,
                Currency = "USD"
            };
            dbContext.LedgerAccounts.Add(account);
            dbContext.Wallets.Add(new UserWallet
            {
                UserId = "protected-wallet",
                Balance = 100m,
                LedgerAccountId = account.Id
            });
            await dbContext.SaveChangesAsync();

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                dbContext.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE user_wallets
                    SET "Balance" = 90
                    WHERE "UserId" = 'protected-wallet'
                    """));

            Assert.Equal(
                PostgresErrorCodes.ObjectNotInPrerequisiteState,
                exception.SqlState);
        }
    }
}
