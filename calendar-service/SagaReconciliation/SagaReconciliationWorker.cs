using calendar_service.Model;
using calendar_service.Services.Clients;
using calendar_service.Services.Contracts;

namespace calendar_service.SagaReconciliation
{
    /// <summary>
    /// Recovers booking-payment sagas left in STARTED after a crash or slow response — see
    /// PAYMENT_SAGA_SPEC.md, "Reconciliation job". Runs an initial pass shortly after startup,
    /// then periodically (open question #3: resolved as "both", since a periodic sweep catches
    /// sagas stuck without a restart, not just ones left over from the last crash).
    /// </summary>
    public class SagaReconciliationWorker : BackgroundService
    {
        private readonly ILogger<SagaReconciliationWorker> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _stuckThreshold;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _claimTtl;

        public SagaReconciliationWorker(
            ILogger<SagaReconciliationWorker> logger,
            IServiceProvider serviceProvider,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;

            // 30s default per PAYMENT_SAGA_SPEC.md's open question #2 — payment-service has no
            // external dependency of its own today, so a STARTED row older than that is either
            // mid-flight (rare) or genuinely stuck.
            _stuckThreshold = TimeSpan.FromSeconds(configuration.GetValue("SagaReconciliation:StuckThresholdSeconds", 30));
            _pollInterval = TimeSpan.FromSeconds(configuration.GetValue("SagaReconciliation:PollIntervalSeconds", 60));

            // Shorter than the poll interval, so if the claiming replica crashes mid-reconciliation
            // the claim is treated as abandoned and re-claimable well before this or another
            // replica's next pass — while still comfortably covering one GET + booking-update round trip.
            _claimTtl = TimeSpan.FromSeconds(configuration.GetValue("SagaReconciliation:ClaimTtlSeconds", 45));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_pollInterval);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Saga reconciliation pass failed unexpectedly; will retry next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        /// <summary>Runs a single reconciliation pass. Public so it can be invoked directly by tests.</summary>
        public async Task RunOnceAsync(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var sagaStateService = scope.ServiceProvider.GetRequiredService<ISagaStateService>();
            var paymentClient = scope.ServiceProvider.GetRequiredService<IPaymentApiClient>();
            var bookingService = scope.ServiceProvider.GetRequiredService<IBookingService>();

            var stuck = await sagaStateService.FindStuckAsync(_stuckThreshold);
            if (stuck.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Saga reconciliation: found {Count} STARTED saga(s) stuck beyond {Threshold}",
                stuck.Count, _stuckThreshold);

            foreach (var saga in stuck)
            {
                ct.ThrowIfCancellationRequested();
                await ReconcileAsync(saga, sagaStateService, paymentClient, bookingService);
            }
        }

        private async Task ReconcileAsync(
            SagaState saga,
            ISagaStateService sagaStateService,
            IPaymentApiClient paymentClient,
            IBookingService bookingService)
        {
            // Claim this saga before doing any work on it, so that if calendar-service is
            // scaled to multiple replicas — each running its own independent reconciliation
            // timer — only one of them actually calls payment-service and mutates the booking
            // for a given saga per pass. See ISagaStateService.TryClaimAsync.
            var claimed = await sagaStateService.TryClaimAsync(saga.SagaId, _claimTtl);
            if (claimed == null)
            {
                _logger.LogDebug("Saga reconciliation: sagaId={SagaId} already claimed by another instance; skipping this pass", saga.SagaId);
                return;
            }

            PaymentTransactionResult? transaction;
            try
            {
                transaction = await paymentClient.GetTransactionBySagaIdAsync(saga.SagaId, CancellationToken.None);
            }
            catch (PaymentServiceUnavailableException ex)
            {
                // Can't yet tell whether the charge happened, so leave the saga STARTED and
                // retry on the next pass rather than risk marking a real charge as FAILED.
                _logger.LogWarning(ex, "Saga reconciliation: payment-service unreachable for sagaId={SagaId}; will retry next pass", saga.SagaId);
                return;
            }

            if (transaction == null)
            {
                // No transaction was ever recorded by payment-service for this sagaId, so the
                // charge genuinely never happened (e.g. calendar-service crashed before the HTTP
                // call was even made). Safe to mark FAILED — no money was taken.
                await sagaStateService.FailAsync(saga.SagaId, "Reconciliation: no matching payment-service transaction found");
                _logger.LogWarning("Saga reconciliation: sagaId={SagaId} bookingId={BookingId} had no payment-service transaction; marked FAILED",
                    saga.SagaId, saga.BookingId);
                return;
            }

            if (!string.Equals(transaction.Status, PaymentTransactionResult.StatusApproved, StringComparison.OrdinalIgnoreCase))
            {
                await sagaStateService.FailAsync(saga.SagaId, $"Reconciliation: payment-service transaction status was {transaction.Status}");
                return;
            }

            if (transaction.Amount != saga.RequestedAmount)
            {
                // Charged, but not for the amount this saga requested — an operational anomaly
                // that needs a human, not something reconciliation should silently complete.
                _logger.LogError("Saga reconciliation: sagaId={SagaId} charged {Charged} but requested {Requested}; needs manual review",
                    saga.SagaId, transaction.Amount, saga.RequestedAmount);
                await sagaStateService.FailAsync(saga.SagaId,
                    $"Reconciliation: charged amount {transaction.Amount} did not match requested {saga.RequestedAmount}");
                return;
            }

            var booking = await bookingService.GetByIdAsync(saga.BookingId);
            if (booking == null)
            {
                // Charge succeeded but the booking it belonged to is gone — leave the saga
                // STARTED; this needs a human, not an automated FAILED/COMPLETED guess.
                _logger.LogError("Saga reconciliation: sagaId={SagaId} approved charge {TransactionId} but booking {BookingId} no longer exists; needs manual review",
                    saga.SagaId, transaction.Id, saga.BookingId);
                return;
            }

            if (booking.Status == Booking.StatusCompleted)
            {
                // The original request actually finished after all (just slower than the stuck
                // threshold) — just close out the now-redundant saga row.
                await sagaStateService.CompleteAsync(saga.SagaId, transaction.Id);
                return;
            }

            if (booking.Status != Booking.StatusImplemented)
            {
                _logger.LogError("Saga reconciliation: sagaId={SagaId} approved charge {TransactionId} but booking {BookingId} is {Status}, not IMPLEMENTED; needs manual review",
                    saga.SagaId, transaction.Id, saga.BookingId, booking.Status);
                return;
            }

            try
            {
                await bookingService.CompletePaymentAsync(saga.BookingId, booking.RequesterUsername, transaction.Id);
                await sagaStateService.CompleteAsync(saga.SagaId, transaction.Id);
                _logger.LogInformation("Saga reconciliation: completed booking {BookingId} for sagaId={SagaId} transaction={TransactionId}",
                    saga.BookingId, saga.SagaId, transaction.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saga reconciliation: failed to complete booking {BookingId} for sagaId={SagaId}; will retry next pass",
                    saga.BookingId, saga.SagaId);
            }
        }
    }
}
