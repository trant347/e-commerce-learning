using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    public sealed class LedgerAccountService : ILedgerAccountService
    {
        private const int MaxCreationAttempts = 3;

        private readonly PaymentDbContext _dbContext;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<LedgerAccountService> _logger;

        public LedgerAccountService(
            PaymentDbContext dbContext,
            TimeProvider timeProvider,
            ILogger<LedgerAccountService> logger)
        {
            _dbContext = dbContext;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public Task<LedgerAccount> EnsureUserWalletAccountAsync(
            string userId,
            string currency = "USD",
            CancellationToken cancellationToken = default)
        {
            var normalizedUserId = ValidateOwner(userId, nameof(userId));
            var normalizedCurrency = ValidateCurrency(currency);
            return EnsureUserWalletAccountCoreAsync(
                normalizedUserId,
                normalizedCurrency,
                cancellationToken);
        }

        public Task<LedgerAccount> EnsureCustodyAccountAsync(
            string custodyUserId,
            string currency = "USD",
            CancellationToken cancellationToken = default)
        {
            var normalizedUserId = ValidateOwner(
                custodyUserId,
                nameof(custodyUserId));
            var normalizedCurrency = ValidateCurrency(currency);
            return EnsureCustodyAccountCoreAsync(
                normalizedUserId,
                normalizedCurrency,
                cancellationToken);
        }

        public async Task<LedgerAccount> EnsureSystemIssuanceAccountAsync(
            string currency = "USD",
            CancellationToken cancellationToken = default)
        {
            var normalizedCurrency = ValidateCurrency(currency);
            return await EnsureAccountWithRetryAsync(
                ownerUserId: null,
                LedgerAccount.TypeSystemIssuance,
                normalizedCurrency,
                cancellationToken);
        }

        private async Task<LedgerAccount> EnsureUserWalletAccountCoreAsync(
            string userId,
            string currency,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaxCreationAttempts; attempt++)
            {
                var existingAccount = await FindAccountAsync(
                    userId,
                    LedgerAccount.TypeUserWallet,
                    currency,
                    cancellationToken);
                if (existingAccount != null)
                {
                    return existingAccount;
                }

                await using var transaction = await BeginTransactionAsync(
                    cancellationToken);
                try
                {
                    var existingWallet = await _dbContext.Wallets
                        .SingleOrDefaultAsync(
                            wallet => wallet.UserId == userId,
                            cancellationToken);
                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    var issuanceAccount = await FindAccountAsync(
                        ownerUserId: null,
                        LedgerAccount.TypeSystemIssuance,
                        currency,
                        cancellationToken);
                    if (issuanceAccount == null)
                    {
                        issuanceAccount = NewAccount(
                            ownerUserId: null,
                            LedgerAccount.TypeSystemIssuance,
                            currency,
                            now);
                        _dbContext.LedgerAccounts.Add(issuanceAccount);
                    }

                    var userAccount = NewAccount(
                        userId,
                        LedgerAccount.TypeUserWallet,
                        currency,
                        now);
                    _dbContext.LedgerAccounts.Add(userAccount);

                    if (existingWallet == null)
                    {
                        var entry = CreateStartingBalanceEntry(
                            userId,
                            currency,
                            issuanceAccount.Id,
                            userAccount.Id,
                            now);
                        _dbContext.JournalEntries.Add(entry);
                        existingWallet = new UserWallet
                        {
                            UserId = userId,
                            Balance = UserWallet.DefaultStartingBalance,
                            LedgerAccountId = userAccount.Id,
                            ProjectionVersion = 1,
                            LastJournalEntryId = entry.Id,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        _dbContext.Wallets.Add(existingWallet);
                    }
                    else
                    {
                        existingWallet.LedgerAccountId = userAccount.Id;
                        existingWallet.UpdatedAt = now;
                    }

                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await CommitAsync(transaction, cancellationToken);
                    _logger.LogInformation(
                        "Ensured user ledger account userId={UserId} currency={Currency} accountId={AccountId}",
                        userId,
                        currency,
                        userAccount.Id);
                    return userAccount;
                }
                catch (DbUpdateException exception)
                    when (transaction != null
                          && IsUniqueViolation(exception)
                          && attempt < MaxCreationAttempts)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _dbContext.ChangeTracker.Clear();
                }
            }

            throw new InvalidOperationException(
                $"Could not create ledger account for user '{userId}' after concurrent retries.");
        }

        private async Task<LedgerAccount> EnsureCustodyAccountCoreAsync(
            string custodyUserId,
            string currency,
            CancellationToken cancellationToken)
        {
            var account = await EnsureAccountWithRetryAsync(
                custodyUserId,
                LedgerAccount.TypeEscrowCustody,
                currency,
                cancellationToken);
            var wallet = await _dbContext.Wallets.SingleOrDefaultAsync(
                candidate => candidate.UserId == custodyUserId,
                cancellationToken);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            if (wallet == null)
            {
                _dbContext.Wallets.Add(new UserWallet
                {
                    UserId = custodyUserId,
                    Balance = 0m,
                    LedgerAccountId = account.Id,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else if (wallet.LedgerAccountId == null)
            {
                wallet.LedgerAccountId = account.Id;
                wallet.UpdatedAt = now;
            }
            else if (wallet.LedgerAccountId != account.Id)
            {
                throw new InvalidOperationException(
                    $"Custody wallet '{custodyUserId}' is linked to a different ledger account.");
            }

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                _dbContext.ChangeTracker.Clear();
                var winner = await _dbContext.Wallets
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.UserId == custodyUserId
                                     && candidate.LedgerAccountId == account.Id,
                        cancellationToken);
                if (winner == null)
                {
                    throw;
                }
            }

            return account;
        }

        private async Task<LedgerAccount> EnsureAccountWithRetryAsync(
            string? ownerUserId,
            string accountType,
            string currency,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaxCreationAttempts; attempt++)
            {
                var existing = await FindAccountAsync(
                    ownerUserId,
                    accountType,
                    currency,
                    cancellationToken);
                if (existing != null)
                {
                    return existing;
                }

                var account = NewAccount(
                    ownerUserId,
                    accountType,
                    currency,
                    _timeProvider.GetUtcNow().UtcDateTime);
                _dbContext.LedgerAccounts.Add(account);
                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return account;
                }
                catch (DbUpdateException exception)
                    when (IsUniqueViolation(exception)
                          && attempt < MaxCreationAttempts)
                {
                    _dbContext.ChangeTracker.Clear();
                }
            }

            throw new InvalidOperationException(
                $"Could not create {accountType} ledger account after concurrent retries.");
        }

        private Task<LedgerAccount?> FindAccountAsync(
            string? ownerUserId,
            string accountType,
            string currency,
            CancellationToken cancellationToken) =>
            _dbContext.LedgerAccounts.SingleOrDefaultAsync(
                account => account.OwnerUserId == ownerUserId
                           && account.AccountType == accountType
                           && account.Currency == currency,
                cancellationToken);

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

        private static Task CommitAsync(
            IDbContextTransaction? transaction,
            CancellationToken cancellationToken) =>
            transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        private static LedgerAccount NewAccount(
            string? ownerUserId,
            string accountType,
            string currency,
            DateTime createdAt) => new()
        {
            OwnerUserId = ownerUserId,
            AccountType = accountType,
            Currency = currency,
            Status = LedgerAccount.StatusActive,
            CreatedAt = createdAt
        };

        private static JournalEntry CreateStartingBalanceEntry(
            string userId,
            string currency,
            Guid issuanceAccountId,
            Guid userAccountId,
            DateTime createdAt)
        {
            var entry = new JournalEntry
            {
                IdempotencyKey = RegistrationIdempotencyKey(userId, currency),
                Operation = JournalEntry.OperationUserRegistrationCredit,
                Currency = currency,
                Description = "Simulated starting balance for registered user",
                CreatedAt = createdAt
            };
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 1,
                AccountId = issuanceAccountId,
                Direction = JournalLine.DirectionDebit,
                Amount = UserWallet.DefaultStartingBalance,
                CreatedAt = createdAt
            });
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 2,
                AccountId = userAccountId,
                Direction = JournalLine.DirectionCredit,
                Amount = UserWallet.DefaultStartingBalance,
                CreatedAt = createdAt
            });
            return entry;
        }

        private static string RegistrationIdempotencyKey(
            string userId,
            string currency)
        {
            var userHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(userId)));
            return $"USER_REGISTRATION_CREDIT:{currency}:{userHash}";
        }

        private static string ValidateOwner(string owner, string parameterName)
        {
            var normalized = owner?.Trim();
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
            {
                throw new ArgumentException(
                    "Account owner is required and cannot exceed 200 characters.",
                    parameterName);
            }

            return normalized;
        }

        private static string ValidateCurrency(string currency)
        {
            var normalized = currency?.Trim().ToUpperInvariant();
            if (normalized is null
                || normalized.Length != 3
                || normalized.Any(character =>
                    character is < 'A' or > 'Z'))
            {
                throw new ArgumentException(
                    "Currency must be a three-letter code.",
                    nameof(currency));
            }

            return normalized;
        }

        private static bool IsUniqueViolation(DbUpdateException exception) =>
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            };
    }
}
