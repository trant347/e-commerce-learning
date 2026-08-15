using Microsoft.Extensions.Options;
using payment_service.Contracts;
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

        private readonly ILedgerAccountService _ledgerAccounts;
        private readonly ILedgerService _ledger;
        private readonly LegacyPaymentOptions _options;
        private readonly ILogger<WalletSimulationPaymentGateway> _logger;

        public WalletSimulationPaymentGateway(
            ILedgerAccountService ledgerAccounts,
            ILedgerService ledger,
            IOptions<LegacyPaymentOptions> options,
            ILogger<WalletSimulationPaymentGateway> logger)
        {
            _ledgerAccounts = ledgerAccounts;
            _ledger = ledger;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<PaymentGatewayResult> ChargeAsync(
            PaymentRequest request,
            PaymentGatewayContext context,
            CancellationToken ct = default)
        {
            if (IsSimulatedDeclineCard(request.CreditCard.CardNumber))
            {
                return new PaymentGatewayResult
                {
                    Status = PaymentTransaction.StatusDeclined,
                    DeclineReason = "Simulated decline test card"
                };
            }

            if (string.IsNullOrWhiteSpace(request.PayerUserId)
                || string.IsNullOrWhiteSpace(request.PayeeUserId))
            {
                if (_options.AllowUnledgeredPaymentsWithoutParties)
                {
                    _logger.LogWarning(
                        "Approving legacy payment transactionId={TransactionId} outside the ledger because payer or payee is missing",
                        context.PaymentTransactionId);
                    return new PaymentGatewayResult
                    {
                        Status = PaymentTransaction.StatusApproved
                    };
                }

                return new PaymentGatewayResult
                {
                    Status = PaymentTransaction.StatusDeclined,
                    DeclineReason =
                        "PayerUserId and PayeeUserId are required for wallet payments."
                };
            }

            await _ledgerAccounts.EnsureUserWalletAccountAsync(
                request.PayerUserId,
                request.Currency,
                ct);
            await _ledgerAccounts.EnsureUserWalletAccountAsync(
                request.PayeeUserId,
                request.Currency,
                ct);

            try
            {
                await _ledger.PostTransferAsync(
                    new LedgerTransfer
                    {
                        IdempotencyKey = context.IdempotencyKey,
                        PaymentTransactionId = context.PaymentTransactionId,
                        SagaId = request.SagaId,
                        Operation = JournalEntry.OperationLegacyPayment,
                        Currency = request.Currency,
                        Amount = request.Amount,
                        DebitAccount = new LedgerAccountReference(
                            request.PayerUserId,
                            LedgerAccount.TypeUserWallet),
                        CreditAccount = new LedgerAccountReference(
                            request.PayeeUserId,
                            LedgerAccount.TypeUserWallet),
                        Description = "Legacy synchronous wallet payment"
                    },
                    ct);
            }
            catch (InsufficientLedgerFundsException exception)
            {
                _logger.LogInformation(
                    "Declining payment of {Amount} for user {UserId}: balance is only {Balance}",
                    request.Amount,
                    request.PayerUserId,
                    exception.AvailableBalance);
                return new PaymentGatewayResult
                {
                    Status = PaymentTransaction.StatusDeclined,
                    DeclineReason =
                        $"Insufficient balance (your balance is {exception.AvailableBalance:F2} " +
                        $"{request.Currency}, but the charge is {request.Amount:F2} {request.Currency})"
                };
            }

            return new PaymentGatewayResult { Status = PaymentTransaction.StatusApproved };
        }

        public Task<PaymentGatewayResult> ChargeAsync(
            PaymentRequest request,
            CancellationToken ct = default)
        {
            var transactionId = Guid.NewGuid();
            return ChargeAsync(
                request,
                new PaymentGatewayContext(
                    transactionId,
                    $"{JournalEntry.OperationLegacyPayment}:{transactionId:D}"),
                ct);
        }

        private static bool IsSimulatedDeclineCard(string cardNumber) => cardNumber == SimulatedDeclineCardNumber;
    }
}
