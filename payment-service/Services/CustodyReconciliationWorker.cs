using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using payment_service.Data;
using payment_service.Models;
using payment_service.Observability;

namespace payment_service.Services
{
    /// <summary>
    /// Periodically verifies that the custody wallet balance equals the total value of all
    /// funded escrows that have not yet been released or refunded.
    /// </summary>
    public sealed class CustodyReconciliationWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CustodyReconciliationWorker> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly string _custodyUserId;
        private readonly TimeSpan _pollInterval;

        public CustodyReconciliationWorker(
            IServiceProvider serviceProvider,
            ILogger<CustodyReconciliationWorker> logger,
            TimeProvider timeProvider,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _timeProvider = timeProvider;
            _custodyUserId = configuration["Escrow:CustodyUserId"]?.Trim()
                ?? throw new InvalidOperationException(
                    "Escrow:CustodyUserId is required.");
            var pollSeconds = configuration.GetValue(
                "EscrowReconciliation:PollIntervalSeconds",
                60);
            if (pollSeconds <= 0)
            {
                throw new InvalidOperationException(
                    "EscrowReconciliation:PollIntervalSeconds must be greater than zero.");
            }

            _pollInterval = TimeSpan.FromSeconds(pollSeconds);
        }

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_pollInterval);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Custody reconciliation pass failed; retrying on the next poll");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task<CustodyReconciliationResult> RunOnceAsync(
            CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext =
                scope.ServiceProvider.GetRequiredService<PaymentDbContext>();

            // Read the wallet and escrow ledger from one database snapshot so a transfer
            // committed between the two queries cannot create a false mismatch alert.
            await using var transaction = dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.RepeatableRead,
                    cancellationToken)
                : null;
            var escrows = await dbContext.Escrows
                .AsNoTracking()
                .Where(escrow =>
                    escrow.CustodyUserId == _custodyUserId)
                .ToListAsync(cancellationToken);
            var wallets = await dbContext.Wallets
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var accounts = await dbContext.LedgerAccounts
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var entries = await dbContext.JournalEntries
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var lines = await dbContext.JournalLines
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var paymentTransactions = await dbContext.Transactions
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var cutover = await dbContext.LedgerCutoverStates
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            var appendOnlyProtectionMissingCount =
                await CountMissingAppendOnlyProtectionsAsync(
                    dbContext,
                    cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            var linesByEntry = lines
                .GroupBy(line => line.JournalEntryId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var linesByAccount = lines
                .GroupBy(line => line.AccountId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var entriesByPaymentTransaction = entries
                .Where(entry => entry.PaymentTransactionId.HasValue)
                .GroupBy(entry => entry.PaymentTransactionId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());
            var accountsById = accounts.ToDictionary(account => account.Id);
            var journalBalances = accounts.ToDictionary(
                account => account.Id,
                account => CalculateBalance(
                    linesByAccount.GetValueOrDefault(account.Id)));

            var unbalancedEntryCount = entries.Count(entry =>
            {
                var entryLines = linesByEntry.GetValueOrDefault(entry.Id) ?? [];
                return entryLines.Count < 2
                       || entryLines
                           .Where(line => line.Direction == JournalLine.DirectionDebit)
                           .Sum(line => line.Amount)
                       != entryLines
                           .Where(line => line.Direction == JournalLine.DirectionCredit)
                           .Sum(line => line.Amount);
            });
            var projectionMismatchCount = wallets.Count(wallet =>
                wallet.LedgerAccountId.HasValue
                && journalBalances.GetValueOrDefault(
                    wallet.LedgerAccountId.Value) != wallet.Balance);
            var projectionMismatchValue = wallets
                .Where(wallet => wallet.LedgerAccountId.HasValue)
                .Sum(wallet => Math.Abs(
                    wallet.Balance
                    - journalBalances.GetValueOrDefault(
                        wallet.LedgerAccountId!.Value)));
            var negativeBalanceCount = wallets.Count(wallet => wallet.Balance < 0m)
                + accounts.Count(account =>
                    account.AccountType != LedgerAccount.TypeSystemIssuance
                    && journalBalances.GetValueOrDefault(account.Id) < 0m);
            var closedAccountPostingCount = lines.Count(line =>
                accountsById.TryGetValue(line.AccountId, out var account)
                && account.Status == LedgerAccount.StatusClosed);

            var authoritativeTransactions = cutover == null
                ? []
                : paymentTransactions
                    .Where(payment => payment.CreatedAt >= cutover.LedgerEpochAt)
                    .ToList();
            var missingApprovedJournalCount = authoritativeTransactions.Count(
                payment => payment.Status == PaymentTransaction.StatusApproved
                           && IsMoneyMovement(payment)
                           && entriesByPaymentTransaction
                               .GetValueOrDefault(payment.Id)?.Count != 1);
            var declinedJournalCount = authoritativeTransactions.Count(
                payment => payment.Status == PaymentTransaction.StatusDeclined
                           && entriesByPaymentTransaction.ContainsKey(payment.Id));
            var escrowJournalLinkMismatchCount = CountEscrowLinkMismatches(
                escrows,
                authoritativeTransactions,
                entriesByPaymentTransaction,
                cutover?.LedgerEpochAt);
            var entriesByEscrow = entries
                .Where(entry => entry.EscrowId.HasValue)
                .GroupBy(entry => entry.EscrowId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());
            var conflictingEscrowCompletionCount = escrows.Count(escrow =>
                (escrow.ReleaseTransactionId.HasValue
                 && escrow.RefundTransactionId.HasValue)
                || HasBothCompletionPostings(
                    entriesByEscrow.GetValueOrDefault(escrow.Id)));

            var cachedCustodyBalance = wallets
                .Where(wallet => wallet.UserId == _custodyUserId)
                .Select(wallet => (decimal?)wallet.Balance)
                .SingleOrDefault() ?? 0m;
            var custodyAccountId = wallets
                .Where(wallet => wallet.UserId == _custodyUserId)
                .Select(wallet => wallet.LedgerAccountId)
                .SingleOrDefault();
            var custodyBalance = custodyAccountId.HasValue
                ? journalBalances.GetValueOrDefault(custodyAccountId.Value)
                : cachedCustodyBalance;
            var fundedEscrows = escrows
                .Where(escrow => escrow.Status == EscrowRecord.StatusFunded)
                .ToList();
            var expectedBalance = fundedEscrows.Sum(escrow => escrow.Amount);
            var mismatch = custodyBalance - expectedBalance;
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            // Record operational age and value by escrow state even when the aggregate
            // custody invariant is healthy.
            foreach (var escrow in escrows)
            {
                PaymentSagaMetrics.EscrowAge.Record(
                    Math.Max(0, (now - (escrow.FundedAt ?? escrow.UpdatedAt)).TotalSeconds),
                    new KeyValuePair<string, object?>("state", escrow.Status));
                PaymentSagaMetrics.EscrowValue.Record(
                    (double)escrow.Amount,
                    new KeyValuePair<string, object?>("state", escrow.Status),
                    new KeyValuePair<string, object?>("currency", escrow.Currency));
            }
            PaymentSagaMetrics.CustodyMismatch.Record((double)mismatch);
            PaymentSagaMetrics.LedgerProjectionMismatch.Record(
                (double)projectionMismatchValue);
            var ledgerAnomalyCount =
                unbalancedEntryCount
                + projectionMismatchCount
                + missingApprovedJournalCount
                + declinedJournalCount
                + escrowJournalLinkMismatchCount
                + conflictingEscrowCompletionCount
                + negativeBalanceCount
                + closedAccountPostingCount
                + appendOnlyProtectionMissingCount;
            PaymentSagaMetrics.LedgerAnomalies.Record(ledgerAnomalyCount);

            if (mismatch != 0m || ledgerAnomalyCount != 0)
            {
                _logger.LogCritical(
                    "Ledger reconciliation failed custodyUserId={CustodyUserId} custodyBalance={CustodyBalance} cachedCustodyBalance={CachedCustodyBalance} fundedEscrowValue={FundedEscrowValue} difference={Difference} fundedEscrowCount={FundedEscrowCount} unbalancedEntries={UnbalancedEntryCount} projectionMismatches={ProjectionMismatchCount} missingApprovedJournals={MissingApprovedJournalCount} declinedJournals={DeclinedJournalCount} escrowLinkMismatches={EscrowJournalLinkMismatchCount} conflictingEscrowCompletions={ConflictingEscrowCompletionCount} negativeBalances={NegativeBalanceCount} closedAccountPostings={ClosedAccountPostingCount} missingAppendOnlyProtections={AppendOnlyProtectionMissingCount}",
                    _custodyUserId,
                    custodyBalance,
                    cachedCustodyBalance,
                    expectedBalance,
                    mismatch,
                    fundedEscrows.Count,
                    unbalancedEntryCount,
                    projectionMismatchCount,
                    missingApprovedJournalCount,
                    declinedJournalCount,
                    escrowJournalLinkMismatchCount,
                    conflictingEscrowCompletionCount,
                    negativeBalanceCount,
                    closedAccountPostingCount,
                    appendOnlyProtectionMissingCount);
            }
            else
            {
                _logger.LogInformation(
                    "Custody reconciliation balanced custodyUserId={CustodyUserId} walletBalance={WalletBalance} fundedEscrowCount={FundedEscrowCount}",
                    _custodyUserId,
                    custodyBalance,
                    fundedEscrows.Count);
            }

            return new CustodyReconciliationResult(
                custodyBalance,
                expectedBalance,
                fundedEscrows.Count,
                cachedCustodyBalance,
                unbalancedEntryCount,
                projectionMismatchCount,
                projectionMismatchValue,
                missingApprovedJournalCount,
                declinedJournalCount,
                escrowJournalLinkMismatchCount,
                conflictingEscrowCompletionCount,
                negativeBalanceCount,
                closedAccountPostingCount,
                appendOnlyProtectionMissingCount);
        }

        private static decimal CalculateBalance(
            IReadOnlyCollection<JournalLine>? lines) =>
            lines?.Sum(line => line.Direction == JournalLine.DirectionCredit
                ? line.Amount
                : -line.Amount) ?? 0m;

        private static bool IsMoneyMovement(PaymentTransaction transaction) =>
            transaction.Operation is
                JournalEntry.OperationLegacyPayment
                or JournalEntry.OperationFundEscrow
                or JournalEntry.OperationReleaseEscrow
                or JournalEntry.OperationRefundEscrow;

        private static int CountEscrowLinkMismatches(
            IReadOnlyCollection<EscrowRecord> escrows,
            IReadOnlyCollection<PaymentTransaction> authoritativeTransactions,
            IReadOnlyDictionary<Guid, List<JournalEntry>> entriesByPaymentTransaction,
            DateTime? ledgerEpochAt)
        {
            var transactionsById = authoritativeTransactions.ToDictionary(
                transaction => transaction.Id);
            var mismatchCount = 0;
            foreach (var escrow in escrows)
            {
                mismatchCount += IsEscrowLinkValid(
                    escrow,
                    escrow.FundingTransactionId,
                    JournalEntry.OperationFundEscrow,
                    escrow.FundedAt >= ledgerEpochAt,
                    transactionsById,
                    entriesByPaymentTransaction) ? 0 : 1;
                mismatchCount += IsEscrowLinkValid(
                    escrow,
                    escrow.ReleaseTransactionId,
                    JournalEntry.OperationReleaseEscrow,
                    escrow.ReleasedAt >= ledgerEpochAt,
                    transactionsById,
                    entriesByPaymentTransaction) ? 0 : 1;
                mismatchCount += IsEscrowLinkValid(
                    escrow,
                    escrow.RefundTransactionId,
                    JournalEntry.OperationRefundEscrow,
                    escrow.RefundedAt >= ledgerEpochAt,
                    transactionsById,
                    entriesByPaymentTransaction) ? 0 : 1;
            }

            return mismatchCount;
        }

        private static bool IsEscrowLinkValid(
            EscrowRecord escrow,
            Guid? transactionId,
            string operation,
            bool linkRequired,
            IReadOnlyDictionary<Guid, PaymentTransaction> transactionsById,
            IReadOnlyDictionary<Guid, List<JournalEntry>> entriesByPaymentTransaction)
        {
            if (!transactionId.HasValue)
            {
                return !linkRequired;
            }
            if (!transactionsById.TryGetValue(
                    transactionId.Value,
                    out var transaction))
            {
                return !linkRequired;
            }

            var journalEntries =
                entriesByPaymentTransaction.GetValueOrDefault(transaction.Id);
            return transaction.Status == PaymentTransaction.StatusApproved
                   && transaction.EscrowId == escrow.Id
                   && transaction.BookingId == escrow.BookingId
                   && transaction.Operation == operation
                   && transaction.Amount == escrow.Amount
                   && transaction.Currency == escrow.Currency
                   && journalEntries is { Count: 1 }
                   && journalEntries[0].EscrowId == escrow.Id
                   && journalEntries[0].BookingId == escrow.BookingId
                   && journalEntries[0].Operation == operation
                   && journalEntries[0].Currency == escrow.Currency;
        }

        private static bool HasBothCompletionPostings(
            IReadOnlyCollection<JournalEntry>? entries) =>
            entries?.Any(entry =>
                entry.Operation == JournalEntry.OperationReleaseEscrow) == true
            && entries.Any(entry =>
                entry.Operation == JournalEntry.OperationRefundEscrow);

        private static async Task<int> CountMissingAppendOnlyProtectionsAsync(
            PaymentDbContext dbContext,
            CancellationToken cancellationToken)
        {
            if (dbContext.Database.ProviderName
                != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return 0;
            }

            var connection = dbContext.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = dbContext.Database.CurrentTransaction
                ?.GetDbTransaction();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM pg_trigger
                WHERE NOT tgisinternal
                  AND tgname IN (
                      'TR_journal_entries_append_only',
                      'TR_journal_lines_append_only')
                """;
            var count = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken));
            return Math.Max(0, 2 - count);
        }
    }

    /// <summary>
    /// Captures the custody wallet and funded-escrow totals produced by one reconciliation pass.
    /// </summary>
    public sealed record CustodyReconciliationResult(
        decimal CustodyBalance,
        decimal FundedEscrowValue,
        int FundedEscrowCount,
        decimal CachedCustodyBalance,
        int UnbalancedEntryCount,
        int ProjectionMismatchCount,
        decimal ProjectionMismatchValue,
        int MissingApprovedJournalCount,
        int DeclinedJournalCount,
        int EscrowJournalLinkMismatchCount,
        int ConflictingEscrowCompletionCount,
        int NegativeBalanceCount,
        int ClosedAccountPostingCount,
        int AppendOnlyProtectionMissingCount)
    {
        public decimal Difference => CustodyBalance - FundedEscrowValue;
        public bool IsBalanced => Difference == 0m;
        public int LedgerAnomalyCount =>
            UnbalancedEntryCount
            + ProjectionMismatchCount
            + MissingApprovedJournalCount
            + DeclinedJournalCount
            + EscrowJournalLinkMismatchCount
            + ConflictingEscrowCompletionCount
            + NegativeBalanceCount
            + ClosedAccountPostingCount
            + AppendOnlyProtectionMissingCount;
        public bool IsHealthy => IsBalanced && LedgerAnomalyCount == 0;
    }
}
