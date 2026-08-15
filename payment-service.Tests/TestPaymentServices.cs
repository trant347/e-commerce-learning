using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using payment_service.Data;
using payment_service.Services;

namespace payment_service.Tests
{
    internal static class TestPaymentServices
    {
        public static WalletSimulationPaymentGateway CreateLegacyGateway(
            PaymentDbContext dbContext,
            bool allowUnledgeredPaymentsWithoutParties = true)
        {
            var ledgerAccounts = new LedgerAccountService(
                dbContext,
                TimeProvider.System,
                NullLogger<LedgerAccountService>.Instance);
            var ledger = new LedgerService(
                dbContext,
                TimeProvider.System,
                NullLogger<LedgerService>.Instance);
            return new WalletSimulationPaymentGateway(
                ledgerAccounts,
                ledger,
                Options.Create(new LegacyPaymentOptions
                {
                    AllowUnledgeredPaymentsWithoutParties =
                        allowUnledgeredPaymentsWithoutParties
                }),
                NullLogger<WalletSimulationPaymentGateway>.Instance);
        }
    }
}
