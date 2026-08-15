using Microsoft.EntityFrameworkCore;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    public sealed class LedgerQueryService : ILedgerQueryService
    {
        public const int MaximumStatementPageSize = 200;

        private readonly PaymentDbContext _dbContext;

        public LedgerQueryService(PaymentDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<LedgerProjectedBalance> GetProjectedBalanceAsync(
            Guid accountId,
            CancellationToken cancellationToken = default)
        {
            ValidateAccountId(accountId);
            var projection = await _dbContext.Wallets
                .AsNoTracking()
                .Where(wallet => wallet.LedgerAccountId == accountId)
                .Select(wallet => new LedgerProjectedBalance(
                    accountId,
                    wallet.Balance,
                    wallet.ProjectionVersion,
                    wallet.LastJournalEntryId))
                .SingleOrDefaultAsync(cancellationToken);
            if (projection != null)
            {
                return projection;
            }

            await EnsureAccountExistsAsync(accountId, cancellationToken);
            throw new InvalidOperationException(
                $"Ledger account '{accountId}' does not have a wallet projection.");
        }

        public async Task<decimal> GetJournalBalanceAsync(
            Guid accountId,
            CancellationToken cancellationToken = default)
        {
            ValidateAccountId(accountId);
            await EnsureAccountExistsAsync(accountId, cancellationToken);
            return await CalculateJournalBalanceAsync(
                accountId,
                asOf: null,
                cancellationToken);
        }

        public async Task<decimal> GetHistoricalBalanceAsync(
            Guid accountId,
            DateTimeOffset asOf,
            CancellationToken cancellationToken = default)
        {
            ValidateAccountId(accountId);
            await EnsureAccountExistsAsync(accountId, cancellationToken);
            return await CalculateJournalBalanceAsync(
                accountId,
                asOf.UtcDateTime,
                cancellationToken);
        }

        public async Task<LedgerStatementPage> GetStatementAsync(
            Guid accountId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ValidateAccountId(accountId);
            if (pageNumber < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageNumber),
                    "Page number must be at least one.");
            }
            if (pageSize < 1 || pageSize > MaximumStatementPageSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageSize),
                    $"Page size must be between 1 and {MaximumStatementPageSize}.");
            }

            await EnsureAccountExistsAsync(accountId, cancellationToken);
            var rows = await _dbContext.JournalLines
                .AsNoTracking()
                .Where(line => line.AccountId == accountId)
                .OrderBy(line => line.CreatedAt)
                .ThenBy(line => line.JournalEntryId)
                .ThenBy(line => line.LineNumber)
                .Skip(checked((pageNumber - 1) * pageSize))
                .Take(pageSize + 1)
                .Select(line => new LedgerStatementItem(
                    line.JournalEntryId,
                    line.LineNumber,
                    line.CreatedAt,
                    line.Direction,
                    line.Amount,
                    line.Direction == JournalLine.DirectionCredit
                        ? line.Amount
                        : -line.Amount,
                    line.JournalEntry.Operation,
                    line.JournalEntry.Currency,
                    line.JournalEntry.Description,
                    line.JournalEntry.PaymentTransactionId,
                    line.JournalEntry.SagaId,
                    line.JournalEntry.EscrowId,
                    line.JournalEntry.BookingId))
                .ToListAsync(cancellationToken);
            var hasMore = rows.Count > pageSize;
            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            return new LedgerStatementPage(
                rows,
                pageNumber,
                pageSize,
                hasMore);
        }

        private async Task<decimal> CalculateJournalBalanceAsync(
            Guid accountId,
            DateTime? asOf,
            CancellationToken cancellationToken)
        {
            var query = _dbContext.JournalLines
                .AsNoTracking()
                .Where(line => line.AccountId == accountId);
            if (asOf.HasValue)
            {
                query = query.Where(line => line.CreatedAt <= asOf.Value);
            }

            return await query
                .Select(line => (decimal?)(line.Direction == JournalLine.DirectionCredit
                    ? line.Amount
                    : -line.Amount))
                .SumAsync(cancellationToken)
                ?? 0m;
        }

        private async Task EnsureAccountExistsAsync(
            Guid accountId,
            CancellationToken cancellationToken)
        {
            if (!await _dbContext.LedgerAccounts
                    .AsNoTracking()
                    .AnyAsync(
                        account => account.Id == accountId,
                        cancellationToken))
            {
                throw new KeyNotFoundException(
                    $"Ledger account '{accountId}' was not found.");
            }
        }

        private static void ValidateAccountId(Guid accountId)
        {
            if (accountId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Ledger account ID is required.",
                    nameof(accountId));
            }
        }
    }
}
