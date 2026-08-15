using Microsoft.EntityFrameworkCore;
using payment_service.Contracts;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    /// <summary>
    /// SIMULATION ONLY — not a real payment processor. Stands in for a real gateway so declines
    /// can be driven by a practical, reproducible business rule (insufficient wallet balance)
    /// instead of only a scripted "magic" test card number. Every registered user gets a wallet
    /// seeded with <see cref="UserWallet.DefaultStartingBalance"/> (see
    /// UserRegisteredConsumerWorker); a charge is declined if the payer's balance can't cover
    /// it, and approved charges move the amount from the payer's wallet to the payee's wallet.
    /// Runs inside the caller's existing DbContext transaction (see
    /// PaymentService.ProcessPaymentAsync) so the wallet movement and the transaction record are
    /// committed atomically together.
    /// </summary>
    public class WalletSimulationPaymentGateway : IPaymentGateway
    {
        /// <summary>
        /// Dev/testing-only "magic" test card number (mirrors the convention used by real
        /// payment gateways' sandbox test cards) that deterministically simulates a declined
        /// charge regardless of wallet balance, so decline handling can still be exercised via a
        /// normal HTTP request without first having to drain a wallet. Not a secret — documented
        /// here and in PAYMENT_SAGA_SPEC.md.
        /// </summary>
        public const string SimulatedDeclineCardNumber = "4000000000000002";

        private readonly PaymentDbContext _dbContext;
        private readonly ILedgerAccountService _ledgerAccounts;
        private readonly ILogger<WalletSimulationPaymentGateway> _logger;

        public WalletSimulationPaymentGateway(
            PaymentDbContext dbContext,
            ILedgerAccountService ledgerAccounts,
            ILogger<WalletSimulationPaymentGateway> logger)
        {
            _dbContext = dbContext;
            _ledgerAccounts = ledgerAccounts;
            _logger = logger;
        }

        public WalletSimulationPaymentGateway(
            PaymentDbContext dbContext,
            ILogger<WalletSimulationPaymentGateway> logger)
            : this(
                dbContext,
                new LedgerAccountService(
                    dbContext,
                    TimeProvider.System,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<LedgerAccountService>.Instance),
                logger)
        {
        }

        public async Task<PaymentGatewayResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
        {
            if (IsSimulatedDeclineCard(request.CreditCard.CardNumber))
            {
                return new PaymentGatewayResult
                {
                    Status = PaymentTransaction.StatusDeclined,
                    DeclineReason = "Simulated decline test card"
                };
            }

            // Backward-compatible: callers that don't supply a PayerUserId (e.g. older tests, or
            // flows that haven't been updated to pass one yet) skip the wallet-balance check
            // entirely and are approved as before.
            if (string.IsNullOrWhiteSpace(request.PayerUserId))
            {
                return new PaymentGatewayResult { Status = PaymentTransaction.StatusApproved };
            }

            var payerWallet = await GetOrCreateWalletAsync(request.PayerUserId, ct);
            if (request.Amount > payerWallet.Balance)
            {
                _logger.LogInformation(
                    "Declining payment of {Amount} for user {UserId}: balance is only {Balance}",
                    request.Amount, request.PayerUserId, payerWallet.Balance);
                return new PaymentGatewayResult
                {
                    Status = PaymentTransaction.StatusDeclined,
                    DeclineReason = $"Insufficient balance (your balance is {payerWallet.Balance:F2} {request.Currency}, " +
                        $"but the charge is {request.Amount:F2} {request.Currency})"
                };
            }

            payerWallet.Balance -= request.Amount;
            payerWallet.UpdatedAt = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(request.PayeeUserId))
            {
                var payeeWallet = await GetOrCreateWalletAsync(request.PayeeUserId, ct);
                payeeWallet.Balance += request.Amount;
                payeeWallet.UpdatedAt = DateTime.UtcNow;
            }

            return new PaymentGatewayResult { Status = PaymentTransaction.StatusApproved };
        }

        private async Task<UserWallet> GetOrCreateWalletAsync(string userId, CancellationToken ct)
        {
            UserWallet? wallet;
            if (_dbContext.Database.IsRelational())
            {
                // Pessimistic row lock: SELECT ... FOR UPDATE blocks any other transaction from
                // reading-for-update or writing this same wallet row until PaymentService's
                // surrounding transaction commits or rolls back. Without this, two concurrent
                // charges against the same wallet could both read the same balance, both pass
                // the "can afford it" check below, and both deduct — driving the balance
                // negative (or worse, racing each other's writes). Requires an ambient
                // transaction to actually hold the lock across statements; see
                // PaymentService.ProcessPaymentAsync, which begins one before calling the
                // gateway.
                wallet = await _dbContext.Wallets
                    .FromSqlInterpolated($"SELECT * FROM user_wallets WHERE \"UserId\" = {userId} FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
            }
            else
            {
                // The in-memory provider (used by unit tests) doesn't support raw SQL/row
                // locking; tests don't exercise concurrent requests against the same DbContext
                // anyway, so a plain read is sufficient there.
                wallet = await _dbContext.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, ct);
            }

            if (wallet != null)
            {
                return wallet;
            }

            // Safety net: in production a wallet should already exist from the USER_REGISTERED
            // Kafka event (see UserRegisteredConsumerWorker), but lazily create one here so a
            // user who somehow has no wallet on record (e.g. pre-existing/seeded test data) isn't
            // unfairly blocked from ever transacting.
            _logger.LogWarning(
                "No wallet found for user {UserId}; lazily creating one with the default starting balance",
                userId);
            await _ledgerAccounts.EnsureUserWalletAccountAsync(
                userId,
                cancellationToken: ct);
            return await _dbContext.Wallets.SingleAsync(
                candidate => candidate.UserId == userId,
                ct);
        }

        private static bool IsSimulatedDeclineCard(string cardNumber) => cardNumber == SimulatedDeclineCardNumber;
    }
}
