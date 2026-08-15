using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    public sealed class LedgerCutoverService : ILedgerCutoverService
    {
        private readonly PaymentDbContext _dbContext;
        private readonly ILedgerAccountService _ledgerAccounts;
        private readonly ILedgerQueryService _ledgerQueries;
        private readonly LedgerCutoverOptions _options;
        private readonly IConfiguration _configuration;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<LedgerCutoverService> _logger;

        public LedgerCutoverService(
            PaymentDbContext dbContext,
            ILedgerAccountService ledgerAccounts,
            ILedgerQueryService ledgerQueries,
            IOptions<LedgerCutoverOptions> options,
            IConfiguration configuration,
            TimeProvider timeProvider,
            ILogger<LedgerCutoverService> logger)
        {
            _dbContext = dbContext;
            _ledgerAccounts = ledgerAccounts;
            _ledgerQueries = ledgerQueries;
            _options = options.Value;
            _configuration = configuration;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task<LedgerCutoverState> ExecuteAsync(
            CancellationToken cancellationToken = default)
        {
            var existing = await _dbContext.LedgerCutoverStates
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            if (existing != null)
            {
                await VerifyAsync(existing, cancellationToken);
                return existing;
            }

            var currency = NormalizeCurrency(_options.Currency);
            var custodyUserId = _configuration["Escrow:CustodyUserId"]?.Trim()
                ?? throw new InvalidOperationException(
                    "Escrow:CustodyUserId is required for ledger cutover.");
            await using var transaction = await BeginTransactionAsync(
                cancellationToken);
            try
            {
                var epoch = _timeProvider.GetUtcNow().UtcDateTime;
                var wallets = await LockWalletsAsync(cancellationToken);
                existing = await _dbContext.LedgerCutoverStates
                    .AsNoTracking()
                    .SingleOrDefaultAsync(cancellationToken);
                if (existing != null)
                {
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    await VerifyAsync(existing, cancellationToken);
                    return existing;
                }

                var issuance = await _ledgerAccounts
                    .EnsureSystemIssuanceAccountAsync(
                        currency,
                        cancellationToken);

                foreach (var wallet in wallets)
                {
                    var account = string.Equals(
                        wallet.UserId,
                        custodyUserId,
                        StringComparison.OrdinalIgnoreCase)
                        ? await _ledgerAccounts.EnsureCustodyAccountAsync(
                            wallet.UserId,
                            currency,
                            cancellationToken)
                        : await _ledgerAccounts.EnsureUserWalletAccountAsync(
                            wallet.UserId,
                            currency,
                            cancellationToken);
                    var journalBalance = await CalculateBalanceAsync(
                        account.Id,
                        cancellationToken);
                    var openingAmount = wallet.Balance - journalBalance;
                    JournalEntry? openingEntry = null;
                    if (openingAmount != 0m)
                    {
                        openingEntry = CreateOpeningEntry(
                            account,
                            issuance,
                            openingAmount,
                            epoch);
                        _dbContext.JournalEntries.Add(openingEntry);
                    }

                    wallet.LedgerAccountId = account.Id;
                    wallet.ProjectionVersion =
                        await _dbContext.JournalLines.CountAsync(
                            line => line.AccountId == account.Id,
                            cancellationToken)
                        + (openingEntry == null ? 0 : 1);
                    wallet.LastJournalEntryId = openingEntry?.Id
                        ?? await LastJournalEntryIdAsync(
                            account.Id,
                            cancellationToken);
                    wallet.UpdatedAt = epoch;
                }

                var state = new LedgerCutoverState
                {
                    Currency = currency,
                    LedgerEpochAt = epoch,
                    CompletedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    WalletCount = wallets.Count
                };
                _dbContext.LedgerCutoverStates.Add(state);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await VerifyTrackedBalancesAsync(
                    wallets,
                    custodyUserId,
                    cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                _logger.LogInformation(
                    "Ledger cutover completed epoch={LedgerEpochAt} walletCount={WalletCount} currency={Currency}",
                    state.LedgerEpochAt,
                    state.WalletCount,
                    state.Currency);
                return state;
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                throw;
            }
        }

        public async Task VerifyAsync(
            LedgerCutoverState state,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(state);
            var wallets = await _dbContext.Wallets
                .AsNoTracking()
                .OrderBy(wallet => wallet.UserId)
                .ToListAsync(cancellationToken);
            var custodyUserId = _configuration["Escrow:CustodyUserId"]?.Trim()
                ?? throw new InvalidOperationException(
                    "Escrow:CustodyUserId is required for ledger reconciliation.");
            await VerifyBalancesAsync(
                wallets,
                custodyUserId,
                state.WalletCount,
                cancellationToken);
        }

        private async Task VerifyTrackedBalancesAsync(
            IReadOnlyCollection<UserWallet> wallets,
            string custodyUserId,
            CancellationToken cancellationToken)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await VerifyBalancesAsync(
                wallets,
                custodyUserId,
                wallets.Count,
                cancellationToken);
        }

        private async Task VerifyBalancesAsync(
            IReadOnlyCollection<UserWallet> wallets,
            string custodyUserId,
            int expectedWalletCount,
            CancellationToken cancellationToken)
        {
            if (wallets.Count < expectedWalletCount)
            {
                throw new InvalidOperationException(
                    $"Ledger cutover expected at least {expectedWalletCount} wallets but found {wallets.Count}.");
            }

            foreach (var wallet in wallets)
            {
                if (!wallet.LedgerAccountId.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Wallet '{wallet.UserId}' is not linked to a ledger account.");
                }

                var journalBalance = await _ledgerQueries.GetJournalBalanceAsync(
                    wallet.LedgerAccountId.Value,
                    cancellationToken);
                if (journalBalance != wallet.Balance)
                {
                    throw new InvalidOperationException(
                        $"Wallet '{wallet.UserId}' balance {wallet.Balance:F2} does not match " +
                        $"journal balance {journalBalance:F2}.");
                }
            }

            var custody = wallets.SingleOrDefault(wallet => string.Equals(
                wallet.UserId,
                custodyUserId,
                StringComparison.OrdinalIgnoreCase));
            if (custody?.LedgerAccountId == null)
            {
                throw new InvalidOperationException(
                    $"Custody wallet '{custodyUserId}' is missing from ledger cutover.");
            }

            var fundedEscrowValue = await _dbContext.Escrows
                .AsNoTracking()
                .Where(escrow =>
                    escrow.Status == EscrowRecord.StatusFunded
                    && escrow.CustodyUserId == custodyUserId)
                .Select(escrow => (decimal?)escrow.Amount)
                .SumAsync(cancellationToken)
                ?? 0m;
            var custodyJournalBalance =
                await _ledgerQueries.GetJournalBalanceAsync(
                    custody.LedgerAccountId.Value,
                    cancellationToken);
            if (custodyJournalBalance != fundedEscrowValue)
            {
                throw new InvalidOperationException(
                    $"Custody journal balance {custodyJournalBalance:F2} does not match " +
                    $"funded escrow value {fundedEscrowValue:F2}.");
            }
        }

        private async Task<List<UserWallet>> LockWalletsAsync(
            CancellationToken cancellationToken)
        {
            if (UsesPostgres())
            {
                return await _dbContext.Wallets
                    .FromSqlRaw(
                        "SELECT * FROM user_wallets ORDER BY \"UserId\" FOR UPDATE")
                    .ToListAsync(cancellationToken);
            }

            return await _dbContext.Wallets
                .OrderBy(wallet => wallet.UserId)
                .ToListAsync(cancellationToken);
        }

        private async Task<decimal> CalculateBalanceAsync(
            Guid accountId,
            CancellationToken cancellationToken)
        {
            return await _dbContext.JournalLines
                .Where(line => line.AccountId == accountId)
                .Select(line => (decimal?)(line.Direction == JournalLine.DirectionCredit
                    ? line.Amount
                    : -line.Amount))
                .SumAsync(cancellationToken)
                ?? 0m;
        }

        private Task<Guid?> LastJournalEntryIdAsync(
            Guid accountId,
            CancellationToken cancellationToken) =>
            _dbContext.JournalLines
                .Where(line => line.AccountId == accountId)
                .OrderByDescending(line => line.CreatedAt)
                .ThenByDescending(line => line.JournalEntryId)
                .ThenByDescending(line => line.LineNumber)
                .Select(line => (Guid?)line.JournalEntryId)
                .FirstOrDefaultAsync(cancellationToken);

        private static JournalEntry CreateOpeningEntry(
            LedgerAccount walletAccount,
            LedgerAccount issuanceAccount,
            decimal openingAmount,
            DateTime epoch)
        {
            var amount = Math.Abs(openingAmount);
            var walletDirection = openingAmount > 0m
                ? JournalLine.DirectionCredit
                : JournalLine.DirectionDebit;
            var issuanceDirection = openingAmount > 0m
                ? JournalLine.DirectionDebit
                : JournalLine.DirectionCredit;
            var entry = new JournalEntry
            {
                IdempotencyKey =
                    $"{JournalEntry.OperationOpeningBalance}:{walletAccount.Id:D}:{epoch:O}",
                Operation = JournalEntry.OperationOpeningBalance,
                Currency = walletAccount.Currency,
                Description = "Ledger cutover opening balance",
                CreatedAt = epoch
            };
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 1,
                AccountId = issuanceAccount.Id,
                Direction = issuanceDirection,
                Amount = amount,
                CreatedAt = epoch
            });
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 2,
                AccountId = walletAccount.Id,
                Direction = walletDirection,
                Amount = amount,
                CreatedAt = epoch
            });
            return entry;
        }

        private async Task<IDbContextTransaction?> BeginTransactionAsync(
            CancellationToken cancellationToken)
        {
            if (!_dbContext.Database.IsRelational()
                || _dbContext.Database.CurrentTransaction != null)
            {
                return null;
            }

            return await _dbContext.Database.BeginTransactionAsync(
                cancellationToken);
        }

        private static string NormalizeCurrency(string currency)
        {
            var normalized = currency?.Trim().ToUpperInvariant();
            if (normalized is null
                || normalized.Length != 3
                || normalized.Any(character => character is < 'A' or > 'Z'))
            {
                throw new InvalidOperationException(
                    "LedgerCutover:Currency must be a three-letter code.");
            }

            return normalized;
        }

        private bool UsesPostgres() =>
            _dbContext.Database.ProviderName
                == "Npgsql.EntityFrameworkCore.PostgreSQL";
    }
}
