using Microsoft.EntityFrameworkCore;
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
            var custodyBalance = await dbContext.Wallets
                .AsNoTracking()
                .Where(wallet => wallet.UserId == _custodyUserId)
                .Select(wallet => (decimal?)wallet.Balance)
                .SingleOrDefaultAsync(cancellationToken) ?? 0m;
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
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

            if (mismatch != 0m)
            {
                _logger.LogCritical(
                    "Custody reconciliation mismatch custodyUserId={CustodyUserId} walletBalance={WalletBalance} fundedEscrowValue={FundedEscrowValue} difference={Difference} fundedEscrowCount={FundedEscrowCount}",
                    _custodyUserId,
                    custodyBalance,
                    expectedBalance,
                    mismatch,
                    fundedEscrows.Count);
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
                fundedEscrows.Count);
        }
    }

    /// <summary>
    /// Captures the custody wallet and funded-escrow totals produced by one reconciliation pass.
    /// </summary>
    public sealed record CustodyReconciliationResult(
        decimal CustodyBalance,
        decimal FundedEscrowValue,
        int FundedEscrowCount)
    {
        public decimal Difference => CustodyBalance - FundedEscrowValue;
        public bool IsBalanced => Difference == 0m;
    }
}
