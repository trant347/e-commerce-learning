using calendar_service.Model;
using calendar_service.SagaReconciliation;
using calendar_service.Services.Clients;
using calendar_service.Services.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace calendar_service.Tests
{
    /// <summary>
    /// Unit tests for <see cref="SagaReconciliationWorker.RunOnceAsync"/> — the reconciliation
    /// pass added for PAYMENT_SAGA_SPEC.md's migration step 5. Dependencies are resolved through
    /// a real <see cref="IServiceProvider"/> (mirroring how the worker creates its own DI scope
    /// per pass) but backed by mocked services, so no real Mongo/HTTP calls are made.
    /// </summary>
    public class SagaReconciliationWorkerTests
    {
        private static SagaState NewStuckSaga(string bookingId, decimal amount, Guid? sagaId = null) => new()
        {
            SagaId = sagaId ?? Guid.NewGuid(),
            BookingId = bookingId,
            Status = SagaState.StatusStarted,
            RequestedAmount = amount,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        private static (SagaReconciliationWorker Worker, Mock<ISagaStateService> SagaState, Mock<IPaymentApiClient> PaymentClient, Mock<IBookingService> BookingService)
            BuildWorker()
        {
            var sagaState = new Mock<ISagaStateService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var bookingService = new Mock<IBookingService>();

            // Default: claim always succeeds (echoes back whichever saga was passed in), so
            // existing tests that don't care about the claim-lock still exercise the rest of
            // ReconcileAsync's logic. Tests that specifically want a claim to be denied override
            // this per-sagaId.
            sagaState.Setup(s => s.TryClaimAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync((Guid sagaId, TimeSpan _) => new SagaState { SagaId = sagaId, Status = SagaState.StatusStarted });

            var services = new ServiceCollection();
            services.AddSingleton(sagaState.Object);
            services.AddSingleton(paymentClient.Object);
            services.AddSingleton(bookingService.Object);
            var provider = services.BuildServiceProvider();

            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var worker = new SagaReconciliationWorker(NullLogger<SagaReconciliationWorker>.Instance, provider, config);
            return (worker, sagaState, paymentClient, bookingService);
        }

        [Fact]
        public async Task RunOnceAsync_NoStuckSagas_DoesNothing()
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<SagaState>());

            await worker.RunOnceAsync(CancellationToken.None);

            paymentClient.Verify(p => p.GetTransactionBySagaIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RunOnceAsync_ApprovedChargeAndBookingImplemented_CompletesBookingAndSaga()
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewStuckSaga("bk-1", 100m);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<SagaState> { saga });
            paymentClient.Setup(p => p.GetTransactionBySagaIdAsync(saga.SagaId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = PaymentTransactionResult.StatusApproved, SagaId = saga.SagaId });
            bookingService.Setup(b => b.GetByIdAsync("bk-1")).ReturnsAsync(new Booking
            {
                Id = "bk-1",
                RequesterUsername = "alice",
                Status = Booking.StatusImplemented,
                InvoiceAmount = 100m
            });

            await worker.RunOnceAsync(CancellationToken.None);

            bookingService.Verify(b => b.CompletePaymentAsync("bk-1", "alice", "txn-1"), Times.Once);
            sagaState.Verify(s => s.CompleteAsync(saga.SagaId, "txn-1"), Times.Once);
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RunOnceAsync_NoMatchingTransaction_MarksSagaFailed()
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewStuckSaga("bk-1", 100m);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<SagaState> { saga });
            paymentClient.Setup(p => p.GetTransactionBySagaIdAsync(saga.SagaId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((PaymentTransactionResult?)null);

            await worker.RunOnceAsync(CancellationToken.None);

            sagaState.Verify(s => s.FailAsync(saga.SagaId, It.IsAny<string>()), Times.Once);
            bookingService.Verify(b => b.CompletePaymentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RunOnceAsync_PaymentServiceUnreachable_LeavesSagaStarted()
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewStuckSaga("bk-1", 100m);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<SagaState> { saga });
            paymentClient.Setup(p => p.GetTransactionBySagaIdAsync(saga.SagaId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PaymentServiceUnavailableException("unreachable"));

            await worker.RunOnceAsync(CancellationToken.None);

            // Can't be sure whether the charge happened, so neither FAILED nor COMPLETED — left
            // STARTED for the next pass rather than risk mislabeling a real charge.
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RunOnceAsync_DeclinedTransaction_MarksSagaFailed()
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewStuckSaga("bk-1", 100m);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<SagaState> { saga });
            paymentClient.Setup(p => p.GetTransactionBySagaIdAsync(saga.SagaId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = "DECLINED", SagaId = saga.SagaId });

            await worker.RunOnceAsync(CancellationToken.None);

            sagaState.Verify(s => s.FailAsync(saga.SagaId, It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task RunOnceAsync_AmountMismatch_MarksSagaFailed_DoesNotCompleteBooking()
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewStuckSaga("bk-1", 100m);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<SagaState> { saga });
            paymentClient.Setup(p => p.GetTransactionBySagaIdAsync(saga.SagaId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 999m, Status = PaymentTransactionResult.StatusApproved, SagaId = saga.SagaId });

            await worker.RunOnceAsync(CancellationToken.None);

            sagaState.Verify(s => s.FailAsync(saga.SagaId, It.IsAny<string>()), Times.Once);
            bookingService.Verify(b => b.CompletePaymentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RunOnceAsync_BookingAlreadyCompleted_ClosesOutSagaWithoutReapplyingPayment()
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewStuckSaga("bk-1", 100m);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<SagaState> { saga });
            paymentClient.Setup(p => p.GetTransactionBySagaIdAsync(saga.SagaId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = PaymentTransactionResult.StatusApproved, SagaId = saga.SagaId });
            bookingService.Setup(b => b.GetByIdAsync("bk-1")).ReturnsAsync(new Booking
            {
                Id = "bk-1",
                RequesterUsername = "alice",
                Status = Booking.StatusCompleted,
                InvoiceAmount = 100m
            });

            await worker.RunOnceAsync(CancellationToken.None);

            bookingService.Verify(b => b.CompletePaymentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.CompleteAsync(saga.SagaId, "txn-1"), Times.Once);
        }

        [Fact]
        public async Task RunOnceAsync_BookingMissing_LeavesSagaStartedForManualReview()
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewStuckSaga("bk-1", 100m);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<SagaState> { saga });
            paymentClient.Setup(p => p.GetTransactionBySagaIdAsync(saga.SagaId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = PaymentTransactionResult.StatusApproved, SagaId = saga.SagaId });
            bookingService.Setup(b => b.GetByIdAsync("bk-1")).ReturnsAsync((Booking?)null);

            await worker.RunOnceAsync(CancellationToken.None);

            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RunOnceAsync_SagaAlreadyClaimedByAnotherInstance_SkipsWithoutCallingPaymentService()
        {
            // Simulates a second calendar-service replica's reconciliation pass racing this one:
            // TryClaimAsync returns null (another instance holds a live claim), so this pass must
            // not touch payment-service or mutate the saga/booking at all.
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewStuckSaga("bk-1", 100m);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<SagaState> { saga });
            sagaState.Setup(s => s.TryClaimAsync(saga.SagaId, It.IsAny<TimeSpan>())).ReturnsAsync((SagaState?)null);

            await worker.RunOnceAsync(CancellationToken.None);

            paymentClient.Verify(p => p.GetTransactionBySagaIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            bookingService.Verify(b => b.CompletePaymentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData(SagaDispatchStatus.PENDING)]
        [InlineData(SagaDispatchStatus.CLAIMED)]
        public async Task RunOnceAsync_UndispatchedEscrowSaga_LeavesStartedForOutboxRecovery(
            SagaDispatchStatus dispatchStatus)
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewEscrowSaga(dispatchStatus);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>()))
                .ReturnsAsync(new List<SagaState> { saga });
            sagaState.Setup(s => s.TryClaimAsync(saga.SagaId, It.IsAny<TimeSpan>()))
                .ReturnsAsync(saga);

            await worker.RunOnceAsync(CancellationToken.None);

            paymentClient.Verify(
                p => p.GetTransactionBySagaIdAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            sagaState.Verify(
                s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task RunOnceAsync_DispatchedEscrowSaga_LeavesStartedForResultRecovery()
        {
            var (worker, sagaState, paymentClient, bookingService) = BuildWorker();
            var saga = NewEscrowSaga(SagaDispatchStatus.DISPATCHED);
            saga.DispatchedAt = DateTime.UtcNow.AddMinutes(-4);
            sagaState.Setup(s => s.FindStuckAsync(It.IsAny<TimeSpan>()))
                .ReturnsAsync(new List<SagaState> { saga });
            sagaState.Setup(s => s.TryClaimAsync(saga.SagaId, It.IsAny<TimeSpan>()))
                .ReturnsAsync(saga);

            await worker.RunOnceAsync(CancellationToken.None);

            paymentClient.Verify(
                p => p.GetTransactionBySagaIdAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            sagaState.Verify(
                s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()),
                Times.Never);
            sagaState.Verify(
                s => s.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>()),
                Times.Never);
        }

        private static SagaState NewEscrowSaga(
            SagaDispatchStatus dispatchStatus)
        {
            var saga = NewStuckSaga("bk-escrow", 100m);
            saga.EscrowId = Guid.NewGuid();
            saga.Operation = Payment.Contracts.V1.PaymentOperation.FundEscrow;
            saga.DispatchStatus = dispatchStatus;
            saga.PaymentRequest = new PendingPaymentRequest
            {
                SagaId = saga.SagaId,
                EscrowId = saga.EscrowId.Value,
                BookingId = saga.BookingId,
                Operation = saga.Operation,
                Amount = saga.RequestedAmount,
                Currency = "USD",
                PayerUserId = "requester",
                PayeeUserId = "admin-custody",
                TaskMasterUserId = "taskmaster",
                PaymentMethodToken = "pmt_token"
            };
            return saga;
        }
    }
}
