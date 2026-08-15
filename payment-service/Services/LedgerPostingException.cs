namespace payment_service.Services
{
    public sealed class LedgerPostingConflictException : InvalidOperationException
    {
        public LedgerPostingConflictException(string message)
            : base(message)
        {
        }
    }

    public sealed class InsufficientLedgerFundsException : InvalidOperationException
    {
        public InsufficientLedgerFundsException(
            Guid accountId,
            decimal availableBalance,
            decimal requestedAmount)
            : base(
                $"Ledger account '{accountId}' has balance {availableBalance:F2}, " +
                $"which is insufficient for debit {requestedAmount:F2}.")
        {
            AccountId = accountId;
            AvailableBalance = availableBalance;
            RequestedAmount = requestedAmount;
        }

        public Guid AccountId { get; }

        public decimal AvailableBalance { get; }

        public decimal RequestedAmount { get; }
    }

    public sealed class LedgerAccountUnavailableException : InvalidOperationException
    {
        public LedgerAccountUnavailableException(string message)
            : base(message)
        {
        }
    }
}
