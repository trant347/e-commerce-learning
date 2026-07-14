using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using payment_service.Contracts;
using payment_service.Data;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    /// <summary>
    /// Covers WalletSimulationPaymentGateway: the practical, reproducible "insufficient
    /// balance" decline path that replaces relying solely on the scripted magic decline card,
    /// plus the payer-debit/payee-credit money movement for an approved charge.
    /// </summary>
    public class WalletSimulationPaymentGatewayTests
    {
        private static PaymentDbContext NewInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new PaymentDbContext(options);
        }

        private static PaymentRequest NewRequest(decimal amount, string? payerUserId = null, string? payeeUserId = null) => new()
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
            PayerUserId = payerUserId,
            PayeeUserId = payeeUserId
        };

        [Fact]
        public async Task ChargeAsync_PayerHasSufficientBalance_ApprovesAndMovesFundsToPayee()
        {
            await using var dbContext = NewInMemoryContext();
            dbContext.Wallets.Add(new UserWallet { UserId = "alice", Balance = 1000m });
            dbContext.Wallets.Add(new UserWallet { UserId = "bob", Balance = 1000m });
            await dbContext.SaveChangesAsync();
            var gateway = new WalletSimulationPaymentGateway(dbContext, NullLogger<WalletSimulationPaymentGateway>.Instance);

            var result = await gateway.ChargeAsync(NewRequest(300m, payerUserId: "alice", payeeUserId: "bob"));
            await dbContext.SaveChangesAsync();

            Assert.Equal(PaymentTransaction.StatusApproved, result.Status);
            Assert.Equal(700m, (await dbContext.Wallets.SingleAsync(w => w.UserId == "alice")).Balance);
            Assert.Equal(1300m, (await dbContext.Wallets.SingleAsync(w => w.UserId == "bob")).Balance);
        }

        [Fact]
        public async Task ChargeAsync_AmountExceedsPayerBalance_DeclinesAndDoesNotMoveFunds()
        {
            await using var dbContext = NewInMemoryContext();
            dbContext.Wallets.Add(new UserWallet { UserId = "alice", Balance = 100m });
            dbContext.Wallets.Add(new UserWallet { UserId = "bob", Balance = 1000m });
            await dbContext.SaveChangesAsync();
            var gateway = new WalletSimulationPaymentGateway(dbContext, NullLogger<WalletSimulationPaymentGateway>.Instance);

            var result = await gateway.ChargeAsync(NewRequest(300m, payerUserId: "alice", payeeUserId: "bob"));
            await dbContext.SaveChangesAsync();

            Assert.Equal(PaymentTransaction.StatusDeclined, result.Status);
            Assert.Equal(100m, (await dbContext.Wallets.SingleAsync(w => w.UserId == "alice")).Balance);
            Assert.Equal(1000m, (await dbContext.Wallets.SingleAsync(w => w.UserId == "bob")).Balance);
        }

        [Fact]
        public async Task ChargeAsync_SimulatedDeclineCard_DeclinesRegardlessOfBalance()
        {
            await using var dbContext = NewInMemoryContext();
            dbContext.Wallets.Add(new UserWallet { UserId = "alice", Balance = 1000m });
            await dbContext.SaveChangesAsync();
            var gateway = new WalletSimulationPaymentGateway(dbContext, NullLogger<WalletSimulationPaymentGateway>.Instance);

            var request = NewRequest(10m, payerUserId: "alice");
            request.CreditCard.CardNumber = WalletSimulationPaymentGateway.SimulatedDeclineCardNumber;

            var result = await gateway.ChargeAsync(request);
            await dbContext.SaveChangesAsync();

            Assert.Equal(PaymentTransaction.StatusDeclined, result.Status);
            Assert.Equal(1000m, (await dbContext.Wallets.SingleAsync(w => w.UserId == "alice")).Balance);
        }

        [Fact]
        public async Task ChargeAsync_NoPayerUserId_SkipsBalanceCheckAndApproves()
        {
            await using var dbContext = NewInMemoryContext();
            var gateway = new WalletSimulationPaymentGateway(dbContext, NullLogger<WalletSimulationPaymentGateway>.Instance);

            var result = await gateway.ChargeAsync(NewRequest(1_000_000m));

            Assert.Equal(PaymentTransaction.StatusApproved, result.Status);
            Assert.Equal(0, await dbContext.Wallets.CountAsync());
        }

        [Fact]
        public async Task ChargeAsync_PayerWithNoExistingWallet_LazilyCreatesOneWithDefaultBalance()
        {
            await using var dbContext = NewInMemoryContext();
            var gateway = new WalletSimulationPaymentGateway(dbContext, NullLogger<WalletSimulationPaymentGateway>.Instance);

            var result = await gateway.ChargeAsync(NewRequest(200m, payerUserId: "newuser"));
            await dbContext.SaveChangesAsync();

            Assert.Equal(PaymentTransaction.StatusApproved, result.Status);
            var wallet = await dbContext.Wallets.SingleAsync(w => w.UserId == "newuser");
            Assert.Equal(UserWallet.DefaultStartingBalance - 200m, wallet.Balance);
        }
    }
}
