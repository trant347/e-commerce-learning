using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using payment_service.Data;
using payment_service.Models;

namespace payment_service.Services
{
    public sealed class LedgerService : ILedgerService
    {
        private static readonly HashSet<string> s_transferOperations =
        [
            JournalEntry.OperationOpeningBalance,
            JournalEntry.OperationUserRegistrationCredit,
            JournalEntry.OperationLegacyPayment,
            JournalEntry.OperationFundEscrow,
            JournalEntry.OperationReleaseEscrow,
            JournalEntry.OperationRefundEscrow,
            JournalEntry.OperationAdminAdjustment
        ];

        private static readonly HashSet<string> s_accountTypes =
        [
            LedgerAccount.TypeUserWallet,
            LedgerAccount.TypeEscrowCustody,
            LedgerAccount.TypeSystemIssuance
        ];

        private readonly PaymentDbContext _dbContext;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<LedgerService> _logger;

        public LedgerService(
            PaymentDbContext dbContext,
            TimeProvider timeProvider,
            ILogger<LedgerService> logger)
        {
            _dbContext = dbContext;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task<LedgerPostingResult> PostTransferAsync(
            LedgerTransfer transfer,
            CancellationToken cancellationToken = default)
        {
            var normalized = ValidateAndNormalize(transfer);
            await using var ownedTransaction = await BeginOwnedTransactionAsync(
                cancellationToken);

            try
            {
                var accounts = await ResolveAndLockAccountsAsync(
                    normalized,
                    cancellationToken);
                var projections = await LockProjectionsAsync(
                    accounts,
                    cancellationToken);

                var existing = await FindPostingAsync(
                    normalized.IdempotencyKey,
                    cancellationToken);
                if (existing != null)
                {
                    EnsurePostingMatches(
                        existing,
                        normalized,
                        accounts.Debit.Id,
                        accounts.Credit.Id);
                    await CommitOwnedTransactionAsync(
                        ownedTransaction,
                        cancellationToken);
                    return new LedgerPostingResult(existing, WasAlreadyPosted: true);
                }

                EnsureAccountCanPost(accounts.Debit);
                EnsureAccountCanPost(accounts.Credit);

                var debitProjection = GetRequiredProjection(
                    accounts.Debit,
                    projections);
                var creditProjection = GetRequiredProjection(
                    accounts.Credit,
                    projections);
                if (debitProjection != null
                    && debitProjection.Balance < normalized.Amount)
                {
                    throw new InsufficientLedgerFundsException(
                        accounts.Debit.Id,
                        debitProjection.Balance,
                        normalized.Amount);
                }

                var postedAt = _timeProvider.GetUtcNow().UtcDateTime;
                var entry = CreateEntry(
                    normalized,
                    accounts.Debit.Id,
                    accounts.Credit.Id,
                    postedAt);
                _dbContext.JournalEntries.Add(entry);
                var debitSnapshot = ProjectionSnapshot.Capture(debitProjection);
                var creditSnapshot = ProjectionSnapshot.Capture(creditProjection);
                ApplyProjection(
                    debitProjection,
                    entry.Id,
                    -normalized.Amount,
                    postedAt);
                ApplyProjection(
                    creditProjection,
                    entry.Id,
                    normalized.Amount,
                    postedAt);

                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException exception)
                    when (IsUniqueViolation(exception))
                {
                    foreach (var line in entry.Lines)
                    {
                        _dbContext.Entry(line).State = EntityState.Detached;
                    }
                    _dbContext.Entry(entry).State = EntityState.Detached;
                    debitSnapshot.Restore();
                    creditSnapshot.Restore();

                    var winner = await FindPostingAsync(
                        normalized.IdempotencyKey,
                        cancellationToken);
                    if (winner == null)
                    {
                        throw;
                    }

                    EnsurePostingMatches(
                        winner,
                        normalized,
                        accounts.Debit.Id,
                        accounts.Credit.Id);
                    await CommitOwnedTransactionAsync(
                        ownedTransaction,
                        cancellationToken);
                    return new LedgerPostingResult(winner, WasAlreadyPosted: true);
                }

                await CommitOwnedTransactionAsync(
                    ownedTransaction,
                    cancellationToken);
                _logger.LogInformation(
                    "Posted ledger transfer journalEntryId={JournalEntryId} operation={Operation} amount={Amount} currency={Currency}",
                    entry.Id,
                    entry.Operation,
                    normalized.Amount,
                    normalized.Currency);
                return new LedgerPostingResult(entry, WasAlreadyPosted: false);
            }
            catch
            {
                if (ownedTransaction != null)
                {
                    await ownedTransaction.RollbackAsync(cancellationToken);
                }

                throw;
            }
        }

        private async Task<AccountPair> ResolveAndLockAccountsAsync(
            NormalizedTransfer transfer,
            CancellationToken cancellationToken)
        {
            var debit = await FindAccountAsync(
                transfer.DebitAccount,
                transfer.Currency,
                cancellationToken);
            var credit = await FindAccountAsync(
                transfer.CreditAccount,
                transfer.Currency,
                cancellationToken);
            if (debit.Id == credit.Id)
            {
                throw new ArgumentException(
                    "Debit and credit accounts must be distinct.",
                    nameof(transfer));
            }

            var accountIds = new[] { debit.Id, credit.Id }
                .Order()
                .ToArray();
            var locked = new Dictionary<Guid, LedgerAccount>();
            foreach (var accountId in accountIds)
            {
                LedgerAccount account;
                if (UsesPostgres())
                {
                    account = await _dbContext.LedgerAccounts
                        .FromSqlInterpolated(
                            $"SELECT * FROM ledger_accounts WHERE \"Id\" = {accountId} FOR UPDATE")
                        .SingleAsync(cancellationToken);
                }
                else
                {
                    account = await _dbContext.LedgerAccounts.SingleAsync(
                        candidate => candidate.Id == accountId,
                        cancellationToken);
                }

                locked.Add(account.Id, account);
            }

            return new AccountPair(locked[debit.Id], locked[credit.Id]);
        }

        private async Task<Dictionary<Guid, UserWallet>> LockProjectionsAsync(
            AccountPair accounts,
            CancellationToken cancellationToken)
        {
            var projections = new Dictionary<Guid, UserWallet>();
            foreach (var account in new[] { accounts.Debit, accounts.Credit }
                         .OrderBy(candidate => candidate.Id))
            {
                if (account.AccountType == LedgerAccount.TypeSystemIssuance)
                {
                    continue;
                }

                UserWallet? projection;
                if (UsesPostgres())
                {
                    projection = await _dbContext.Wallets
                        .FromSqlInterpolated(
                            $"SELECT * FROM user_wallets WHERE \"LedgerAccountId\" = {account.Id} FOR UPDATE")
                        .SingleOrDefaultAsync(cancellationToken);
                }
                else
                {
                    projection = await _dbContext.Wallets.SingleOrDefaultAsync(
                        wallet => wallet.LedgerAccountId == account.Id,
                        cancellationToken);
                }

                if (projection == null)
                {
                    throw new LedgerAccountUnavailableException(
                        $"Ledger account '{account.Id}' has no wallet projection.");
                }

                projections.Add(account.Id, projection);
            }

            return projections;
        }

        private async Task<LedgerAccount> FindAccountAsync(
            NormalizedAccountReference reference,
            string currency,
            CancellationToken cancellationToken)
        {
            var account = await _dbContext.LedgerAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    account => account.OwnerUserId == reference.OwnerUserId
                               && account.AccountType == reference.AccountType
                               && account.Currency == currency,
                    cancellationToken);
            return account
                ?? throw new LedgerAccountUnavailableException(
                    $"Ledger account type '{reference.AccountType}' for owner " +
                    $"'{reference.OwnerUserId ?? "<system>"}' and currency '{currency}' was not found.");
        }

        private Task<JournalEntry?> FindPostingAsync(
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            _dbContext.JournalEntries
                .AsNoTracking()
                .Include(entry => entry.Lines)
                .SingleOrDefaultAsync(
                    entry => entry.IdempotencyKey == idempotencyKey,
                    cancellationToken);

        private static UserWallet? GetRequiredProjection(
            LedgerAccount account,
            IReadOnlyDictionary<Guid, UserWallet> projections)
        {
            if (account.AccountType == LedgerAccount.TypeSystemIssuance)
            {
                return null;
            }

            return projections[account.Id];
        }

        private static void EnsureAccountCanPost(LedgerAccount account)
        {
            if (account.Status != LedgerAccount.StatusActive)
            {
                throw new InvalidOperationException(
                    $"Ledger account '{account.Id}' is closed.");
            }
        }

        private static void ApplyProjection(
            UserWallet? projection,
            Guid journalEntryId,
            decimal balanceDelta,
            DateTime postedAt)
        {
            if (projection == null)
            {
                return;
            }

            projection.Balance += balanceDelta;
            projection.ProjectionVersion = checked(projection.ProjectionVersion + 1);
            projection.LastJournalEntryId = journalEntryId;
            projection.UpdatedAt = postedAt;
        }

        private static JournalEntry CreateEntry(
            NormalizedTransfer transfer,
            Guid debitAccountId,
            Guid creditAccountId,
            DateTime postedAt)
        {
            var entry = new JournalEntry
            {
                IdempotencyKey = transfer.IdempotencyKey,
                PaymentTransactionId = transfer.PaymentTransactionId,
                SagaId = transfer.SagaId,
                EscrowId = transfer.EscrowId,
                BookingId = transfer.BookingId,
                Operation = transfer.Operation,
                Currency = transfer.Currency,
                Description = transfer.Description,
                CreatedAt = postedAt
            };
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 1,
                AccountId = debitAccountId,
                Direction = JournalLine.DirectionDebit,
                Amount = transfer.Amount,
                CreatedAt = postedAt
            });
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = 2,
                AccountId = creditAccountId,
                Direction = JournalLine.DirectionCredit,
                Amount = transfer.Amount,
                CreatedAt = postedAt
            });
            return entry;
        }

        private static void EnsurePostingMatches(
            JournalEntry entry,
            NormalizedTransfer transfer,
            Guid debitAccountId,
            Guid creditAccountId)
        {
            var matches =
                entry.PaymentTransactionId == transfer.PaymentTransactionId
                && entry.SagaId == transfer.SagaId
                && entry.EscrowId == transfer.EscrowId
                && entry.BookingId == transfer.BookingId
                && entry.Operation == transfer.Operation
                && entry.Currency == transfer.Currency
                && entry.Description == transfer.Description
                && entry.Lines.Count == 2
                && entry.Lines.Any(line =>
                    line.AccountId == debitAccountId
                    && line.Direction == JournalLine.DirectionDebit
                    && line.Amount == transfer.Amount)
                && entry.Lines.Any(line =>
                    line.AccountId == creditAccountId
                    && line.Direction == JournalLine.DirectionCredit
                    && line.Amount == transfer.Amount);
            if (!matches)
            {
                throw new LedgerPostingConflictException(
                    $"Idempotency key '{transfer.IdempotencyKey}' is already used by a different posting.");
            }
        }

        private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(
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

        private static Task CommitOwnedTransactionAsync(
            IDbContextTransaction? transaction,
            CancellationToken cancellationToken) =>
            transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        private static NormalizedTransfer ValidateAndNormalize(
            LedgerTransfer transfer)
        {
            ArgumentNullException.ThrowIfNull(transfer);
            var idempotencyKey = RequiredText(
                transfer.IdempotencyKey,
                200,
                nameof(transfer.IdempotencyKey));
            var operation = RequiredText(
                transfer.Operation,
                30,
                nameof(transfer.Operation));
            if (!s_transferOperations.Contains(operation))
            {
                throw new ArgumentException(
                    $"Unsupported ledger transfer operation '{operation}'.",
                    nameof(transfer.Operation));
            }

            var currency = RequiredText(
                transfer.Currency,
                3,
                nameof(transfer.Currency)).ToUpperInvariant();
            if (currency.Length != 3
                || currency.Any(character => character is < 'A' or > 'Z'))
            {
                throw new ArgumentException(
                    "Currency must be a three-letter code.",
                    nameof(transfer.Currency));
            }

            if (transfer.Amount <= 0
                || transfer.Amount != decimal.Round(transfer.Amount, 2))
            {
                throw new ArgumentException(
                    "Amount must be positive and contain no more than two decimal places.",
                    nameof(transfer.Amount));
            }

            var debit = NormalizeAccount(
                transfer.DebitAccount,
                nameof(transfer.DebitAccount));
            var credit = NormalizeAccount(
                transfer.CreditAccount,
                nameof(transfer.CreditAccount));
            if (debit == credit)
            {
                throw new ArgumentException(
                    "Debit and credit accounts must be distinct.",
                    nameof(transfer));
            }

            ValidateOptionalGuid(
                transfer.PaymentTransactionId,
                nameof(transfer.PaymentTransactionId));
            ValidateOptionalGuid(transfer.SagaId, nameof(transfer.SagaId));
            ValidateOptionalGuid(transfer.EscrowId, nameof(transfer.EscrowId));

            return new NormalizedTransfer(
                idempotencyKey,
                transfer.PaymentTransactionId,
                transfer.SagaId,
                transfer.EscrowId,
                OptionalText(transfer.BookingId, 100, nameof(transfer.BookingId)),
                operation,
                currency,
                transfer.Amount,
                debit,
                credit,
                RequiredText(
                    transfer.Description,
                    500,
                    nameof(transfer.Description)));
        }

        private static NormalizedAccountReference NormalizeAccount(
            LedgerAccountReference reference,
            string parameterName)
        {
            ArgumentNullException.ThrowIfNull(reference, parameterName);
            var accountType = RequiredText(
                reference.AccountType,
                30,
                $"{parameterName}.{nameof(reference.AccountType)}");
            if (!s_accountTypes.Contains(accountType))
            {
                throw new ArgumentException(
                    $"Unsupported ledger account type '{accountType}'.",
                    parameterName);
            }

            var ownerUserId = OptionalText(
                reference.OwnerUserId,
                200,
                $"{parameterName}.{nameof(reference.OwnerUserId)}");
            if (accountType == LedgerAccount.TypeSystemIssuance)
            {
                if (ownerUserId != null)
                {
                    throw new ArgumentException(
                        "System issuance accounts cannot have an owner.",
                        parameterName);
                }
            }
            else if (ownerUserId == null)
            {
                throw new ArgumentException(
                    "User wallet and custody accounts require an owner.",
                    parameterName);
            }

            return new NormalizedAccountReference(ownerUserId, accountType);
        }

        private static string RequiredText(
            string? value,
            int maximumLength,
            string parameterName)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrWhiteSpace(normalized)
                || normalized.Length > maximumLength)
            {
                throw new ArgumentException(
                    $"{parameterName} is required and cannot exceed {maximumLength} characters.",
                    parameterName);
            }

            return normalized;
        }

        private static string? OptionalText(
            string? value,
            int maximumLength,
            string parameterName)
        {
            if (value == null)
            {
                return null;
            }

            var normalized = value.Trim();
            if (normalized.Length == 0 || normalized.Length > maximumLength)
            {
                throw new ArgumentException(
                    $"{parameterName} cannot be empty or exceed {maximumLength} characters.",
                    parameterName);
            }

            return normalized;
        }

        private static void ValidateOptionalGuid(Guid? value, string parameterName)
        {
            if (value == Guid.Empty)
            {
                throw new ArgumentException(
                    $"{parameterName} cannot be an empty GUID.",
                    parameterName);
            }
        }

        private static bool IsUniqueViolation(DbUpdateException exception) =>
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            };

        private bool UsesPostgres() =>
            _dbContext.Database.ProviderName
                == "Npgsql.EntityFrameworkCore.PostgreSQL";

        private sealed record NormalizedAccountReference(
            string? OwnerUserId,
            string AccountType);

        private sealed record NormalizedTransfer(
            string IdempotencyKey,
            Guid? PaymentTransactionId,
            Guid? SagaId,
            Guid? EscrowId,
            string? BookingId,
            string Operation,
            string Currency,
            decimal Amount,
            NormalizedAccountReference DebitAccount,
            NormalizedAccountReference CreditAccount,
            string Description);

        private sealed record AccountPair(
            LedgerAccount Debit,
            LedgerAccount Credit);

        private sealed record ProjectionSnapshot(
            UserWallet? Projection,
            decimal Balance,
            long ProjectionVersion,
            Guid? LastJournalEntryId,
            DateTime UpdatedAt)
        {
            public static ProjectionSnapshot Capture(UserWallet? projection) =>
                projection == null
                    ? new ProjectionSnapshot(null, 0m, 0, null, default)
                    : new ProjectionSnapshot(
                        projection,
                        projection.Balance,
                        projection.ProjectionVersion,
                        projection.LastJournalEntryId,
                        projection.UpdatedAt);

            public void Restore()
            {
                if (Projection == null)
                {
                    return;
                }

                Projection.Balance = Balance;
                Projection.ProjectionVersion = ProjectionVersion;
                Projection.LastJournalEntryId = LastJournalEntryId;
                Projection.UpdatedAt = UpdatedAt;
                Projection.LastJournalEntry = null;
            }
        }
    }
}
