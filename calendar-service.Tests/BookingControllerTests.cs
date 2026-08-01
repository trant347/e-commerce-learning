using System.Security.Claims;
using calendar_service.Controllers;
using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services;
using calendar_service.Services.Clients;
using calendar_service.Services.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Payment.Contracts.V1;
using Xunit;

namespace calendar_service.Tests
{
    /// <summary>
    /// Unit tests for <see cref="BookingController"/>. The booking service, TaskMaster
    /// lookup client and notification producer are all mocked, so these tests exercise
    /// only the controller's request handling, status-code mapping and notification fan-out.
    /// </summary>
    public class BookingControllerTests
    {
        private const string Caller = "alice";
        private const string OwnerUsername = "bob";
        private const string TaskMasterId = "tm-1";

        // ---- helpers ----

        private static BookingController BuildController(
            Mock<IBookingService> service,
            Mock<ITaskMasterApiClient> taskMasterClient,
            Mock<INotificationProducer> notifications,
            string? username = Caller,
            bool isAdmin = false,
            string? bearer = "test-token",
            Mock<IPaymentApiClient>? paymentClient = null,
            Mock<ISagaStateService>? sagaStateService = null,
            IConfiguration? configuration = null)
        {
            var controller = new BookingController(
                service.Object,
                taskMasterClient.Object,
                (paymentClient ?? new Mock<IPaymentApiClient>()).Object,
                (sagaStateService ?? DefaultSagaStateServiceMock()).Object,
                notifications.Object,
                NullLogger<BookingController>.Instance,
                configuration ?? new ConfigurationBuilder().AddInMemoryCollection().Build());

            var claims = new List<Claim>();
            if (!string.IsNullOrEmpty(username))
            {
                claims.Add(new Claim(ClaimTypes.Name, username));
            }
            if (isAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, "ROLE_ADMIN"));
            }
            var identity = string.IsNullOrEmpty(username)
                ? new ClaimsIdentity()
                : new ClaimsIdentity(claims, authenticationType: "Test");

            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            };
            if (!string.IsNullOrEmpty(bearer))
            {
                httpContext.Request.Headers["Authorization"] = $"Bearer {bearer}";
            }
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        /// <summary>
        /// Default saga-state mock for tests that don't care about saga behavior: StartAsync
        /// returns a real SagaState (as the controller reads its SagaId back out immediately).
        /// </summary>
        private static Mock<ISagaStateService> DefaultSagaStateServiceMock()
        {
            var mock = new Mock<ISagaStateService>();
            mock.Setup(s => s.StartAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<decimal>()))
                .ReturnsAsync((string bookingId, Guid sagaId, decimal amount) => new SagaState
                {
                    SagaId = sagaId,
                    BookingId = bookingId,
                    RequestedAmount = amount
                });
            return mock;
        }

        private static DateTime FutureSlot(int hoursFromNowFloor = 48)
        {
            var t = DateTime.UtcNow.AddHours(hoursFromNowFloor);
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
        }

        [Fact]
        public async Task GetPaymentStatus_RequesterOwner_ReturnsSagaAndCurrentEscrowState()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var sagaId = Guid.NewGuid();
            var escrowId = Guid.NewGuid();
            var updatedAt = DateTime.UtcNow;
            sagaState.Setup(s => s.GetBySagaIdAsync(sagaId)).ReturnsAsync(new SagaState
            {
                SagaId = sagaId,
                BookingId = "bk-1",
                EscrowId = escrowId,
                Operation = PaymentOperation.FundEscrow,
                Status = SagaState.StatusCompleted,
                UpdatedAt = updatedAt
            });
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(new Booking
            {
                Id = "bk-1",
                RequesterUsername = Caller,
                TaskMasterUsername = OwnerUsername,
                EscrowStatus = EscrowStatus.Funded
            });
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState);

            var result = await controller.GetPaymentStatus(sagaId);

            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<PaymentStatusResponseV1>(ok.Value);
            Assert.Equal(sagaId, response.SagaId);
            Assert.Equal("bk-1", response.BookingId);
            Assert.Equal(escrowId, response.EscrowId);
            Assert.Equal(PaymentOperation.FundEscrow, response.Operation);
            Assert.Equal(SagaState.StatusCompleted, response.Status);
            Assert.Equal(EscrowStatus.Funded, response.EscrowStatus);
            Assert.Equal(updatedAt, response.UpdatedAt);
        }

        [Fact]
        public async Task GetPaymentStatus_UnrelatedCaller_Returns403()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var sagaId = Guid.NewGuid();
            sagaState.Setup(s => s.GetBySagaIdAsync(sagaId)).ReturnsAsync(new SagaState
            {
                SagaId = sagaId,
                BookingId = "bk-1",
                EscrowId = Guid.NewGuid(),
                Operation = PaymentOperation.FundEscrow
            });
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(new Booking
            {
                Id = "bk-1",
                RequesterUsername = "carol",
                TaskMasterUsername = OwnerUsername
            });
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState);

            var result = await controller.GetPaymentStatus(sagaId);

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task Get_PopulatesLatestSagaForReloadedFrontend()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var sagaId = Guid.NewGuid();
            var booking = AcceptedBooking(Guid.NewGuid());
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);
            sagaState.Setup(s => s.GetLatestByBookingIdAsync("bk-1")).ReturnsAsync(new SagaState
            {
                SagaId = sagaId,
                BookingId = "bk-1",
                EscrowId = booking.EscrowId,
                Operation = PaymentOperation.FundEscrow,
                Status = SagaState.StatusFailed,
                FailureReason = "Card declined"
            });
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState);

            var result = await controller.Get("bk-1");

            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<Booking>(ok.Value);
            Assert.Equal(sagaId, response.LatestPaymentSagaId);
            Assert.Equal(SagaState.StatusFailed, response.LatestPaymentStatus);
            Assert.Equal(PaymentOperation.FundEscrow, response.LatestPaymentOperation);
            Assert.Equal("Card declined", response.LatestPaymentFailureReason);
            Assert.False(response.PaymentPending);
        }

        // ---- Create ----

        [Fact]
        public async Task Create_HappyPath_Returns200_AndPublishesNotification()
        {
            var service = new Mock<IBookingService>();
            var tmClient = new Mock<ITaskMasterApiClient>();
            var notifications = new Mock<INotificationProducer>();

            var slot = FutureSlot();
            tmClient.Setup(c => c.GetByIdAsync(TaskMasterId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new TaskMasterLookup { Id = TaskMasterId, OwnerUsername = OwnerUsername });

            var created = new Booking
            {
                Id = "bk-1",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = OwnerUsername,
                RequesterUsername = Caller,
                SlotStart = slot,
                DurationHours = 2,
                Status = Booking.StatusPending
            };
            service.Setup(s => s.CreateAsync(TaskMasterId, OwnerUsername, Caller, slot, 2, "hi", null))
                   .ReturnsAsync(created);

            var ctrl = BuildController(service, tmClient, notifications);
            var result = await ctrl.Create(new BookingController.CreateBookingDto
            {
                TaskMasterId = TaskMasterId,
                SlotStart = slot,
                DurationHours = 2,
                Message = "hi"
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(created, ok.Value);
            notifications.Verify(n => n.PublishAsync(It.IsAny<object>()), Times.Once);
        }

        [Fact]
        public async Task Create_WhenServiceThrowsOverlap_Returns409()
        {
            var service = new Mock<IBookingService>();
            var tmClient = new Mock<ITaskMasterApiClient>();
            var notifications = new Mock<INotificationProducer>();

            tmClient.Setup(c => c.GetByIdAsync(TaskMasterId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new TaskMasterLookup { Id = TaskMasterId, OwnerUsername = OwnerUsername });
            service.Setup(s => s.CreateAsync(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<decimal?>()))
                   .ThrowsAsync(new InvalidOperationException("This range overlaps an already-booked slot"));

            var ctrl = BuildController(service, tmClient, notifications);
            var result = await ctrl.Create(new BookingController.CreateBookingDto
            {
                TaskMasterId = TaskMasterId,
                SlotStart = FutureSlot(),
                DurationHours = 2
            });

            Assert.IsType<ConflictObjectResult>(result);
            notifications.Verify(n => n.PublishAsync(It.IsAny<object>()), Times.Never);
        }

        [Fact]
        public async Task Create_WithoutAuthenticatedCaller_Returns401()
        {
            var ctrl = BuildController(
                new Mock<IBookingService>(),
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                username: null);

            var result = await ctrl.Create(new BookingController.CreateBookingDto
            {
                TaskMasterId = TaskMasterId,
                SlotStart = FutureSlot(),
                DurationHours = 1
            });

            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task Create_WhenTaskMasterNotFound_Returns404()
        {
            var tmClient = new Mock<ITaskMasterApiClient>();
            tmClient.Setup(c => c.GetByIdAsync(TaskMasterId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((TaskMasterLookup?)null);

            var ctrl = BuildController(
                new Mock<IBookingService>(),
                tmClient,
                new Mock<INotificationProducer>());

            var result = await ctrl.Create(new BookingController.CreateBookingDto
            {
                TaskMasterId = TaskMasterId,
                SlotStart = FutureSlot(),
                DurationHours = 1
            });

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task Create_WhenTaskMasterHasNoOwner_Returns400()
        {
            var tmClient = new Mock<ITaskMasterApiClient>();
            tmClient.Setup(c => c.GetByIdAsync(TaskMasterId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new TaskMasterLookup { Id = TaskMasterId, OwnerUsername = null });

            var ctrl = BuildController(
                new Mock<IBookingService>(),
                tmClient,
                new Mock<INotificationProducer>());

            var result = await ctrl.Create(new BookingController.CreateBookingDto
            {
                TaskMasterId = TaskMasterId,
                SlotStart = FutureSlot(),
                DurationHours = 1
            });

            Assert.IsType<BadRequestObjectResult>(result);
        }

        // ---- Accept ----

        [Fact]
        public async Task Accept_HappyPath_Returns200_AndPublishesAcceptedAndAutoDeclinedNotifications()
        {
            var service = new Mock<IBookingService>();
            var notifications = new Mock<INotificationProducer>();
            var published = new List<object>();
            notifications.Setup(n => n.PublishAsync(It.IsAny<object>()))
                .Callback<object>(published.Add)
                .Returns(Task.CompletedTask);

            var accepted = new Booking
            {
                Id = "bk-1",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = Caller,        // caller is the owner here
                RequesterUsername = "carol",
                SlotStart = FutureSlot(),
                DurationHours = 2,
                Status = Booking.StatusAccepted
            };
            var declined = new Booking
            {
                Id = "bk-2",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = Caller,
                RequesterUsername = "dave",
                SlotStart = accepted.SlotStart,
                DurationHours = 1,
                Status = Booking.StatusDeclined
            };
            service.Setup(s => s.AcceptAsync("bk-1", Caller, "ok"))
                   .ReturnsAsync(new AcceptResult { Accepted = accepted, AutoDeclined = new() { declined } });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), notifications);
            var result = await ctrl.Accept("bk-1", new BookingController.RespondDto { Message = "ok" });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(accepted, ok.Value);
            // One notification to the accepted requester + one per auto-declined sibling.
            notifications.Verify(n => n.PublishAsync(It.IsAny<object>()), Times.Exactly(2));
            var acceptedNotification = System.Text.Json.JsonSerializer.Serialize(
                published[0]);
            using var document = System.Text.Json.JsonDocument.Parse(
                acceptedNotification);
            Assert.Equal(
                "VIEW_PAYMENT_REQUEST",
                document.RootElement.GetProperty("actionType").GetString());
            Assert.Equal(
                "bk-1",
                document.RootElement
                    .GetProperty("actionPayload")
                    .GetProperty("bookingId")
                    .GetString());
        }

        [Fact]
        public async Task Accept_WhenServiceThrowsUnauthorized_Returns403()
        {
            var service = new Mock<IBookingService>();
            service.Setup(s => s.AcceptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
                   .ThrowsAsync(new UnauthorizedAccessException());

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>());
            var result = await ctrl.Accept("bk-1", null);

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task Accept_WhenBookingMissing_Returns404()
        {
            var service = new Mock<IBookingService>();
            service.Setup(s => s.AcceptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
                   .ThrowsAsync(new KeyNotFoundException());

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>());
            var result = await ctrl.Accept("bk-1", null);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Accept_WhenServiceThrowsConflict_Returns409()
        {
            var service = new Mock<IBookingService>();
            service.Setup(s => s.AcceptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
                   .ThrowsAsync(new InvalidOperationException("This range overlaps an already-accepted booking"));

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>());
            var result = await ctrl.Accept("bk-1", null);

            Assert.IsType<ConflictObjectResult>(result);
        }

        // ---- Decline ----

        [Fact]
        public async Task Decline_HappyPath_Returns200_AndPublishesNotification()
        {
            var service = new Mock<IBookingService>();
            var notifications = new Mock<INotificationProducer>();

            var declined = new Booking
            {
                Id = "bk-1",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = Caller,
                RequesterUsername = "carol",
                SlotStart = FutureSlot(),
                DurationHours = 1,
                Status = Booking.StatusDeclined
            };
            service.Setup(s => s.DeclineAsync("bk-1", Caller, "no thanks"))
                   .ReturnsAsync(declined);

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), notifications);
            var result = await ctrl.Decline("bk-1", new BookingController.RespondDto { Message = "no thanks" });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(declined, ok.Value);
            notifications.Verify(n => n.PublishAsync(It.IsAny<object>()), Times.Once);
        }

        [Fact]
        public async Task Decline_WhenServiceReturnsNull_Returns404()
        {
            var service = new Mock<IBookingService>();
            service.Setup(s => s.DeclineAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
                   .ReturnsAsync((Booking?)null);

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>());
            var result = await ctrl.Decline("bk-1", null);

            Assert.IsType<NotFoundResult>(result);
        }

        // ---- Escrow lifecycle ----

        [Fact]
        public async Task StartWork_FundedBooking_Returns200()
        {
            var service = new Mock<IBookingService>();
            var booking = new Booking
            {
                Id = "bk-1",
                Status = Booking.StatusInProgress,
                EscrowStatus = Payment.Contracts.V1.EscrowStatus.Funded
            };
            service.Setup(s => s.StartWorkAsync("bk-1", Caller)).ReturnsAsync(booking);

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>());

            var result = await controller.StartWork("bk-1");

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(booking, ok.Value);
        }

        [Fact]
        public async Task SubmitProof_EscrowBooking_EnqueuesReleaseAndReturns202()
        {
            var service = new Mock<IBookingService>();
            var notifications = new Mock<INotificationProducer>();
            var sagaState = new Mock<ISagaStateService>();
            var escrowId = Guid.NewGuid();
            var existing = new Booking { Id = "bk-1", EscrowId = escrowId };
            var updated = new Booking
            {
                Id = "bk-1",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = OwnerUsername,
                RequesterUsername = Caller,
                Status = Booking.StatusImplemented,
                EscrowId = escrowId,
                EscrowStatus = Payment.Contracts.V1.EscrowStatus.Funded,
                AgreedAmount = 100m,
                AgreedCurrency = "USD",
                InvoiceAmount = 100m,
                ReleaseRequestedAt = DateTime.UtcNow
            };
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(existing);
            service.Setup(s => s.RequestEscrowReleaseAsync("bk-1", Caller, "proof.jpg"))
                .ReturnsAsync(updated);
            PaymentRequestedV1? enqueued = null;
            sagaState.Setup(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<PaymentRequestedV1, string?, CancellationToken>(
                    (request, _, _) => enqueued = request)
                .ReturnsAsync((PaymentRequestedV1 request, string? _, CancellationToken _) =>
                    new SagaState { SagaId = request.SagaId });

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                notifications,
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());
            var result = await controller.SubmitProof(
                "bk-1",
                new BookingController.SubmitProofDto
                {
                    ProofFileUrl = "proof.jpg",
                    InvoiceAmount = 999m
                });

            var accepted = Assert.IsType<AcceptedResult>(result);
            var response = Assert.IsType<PaymentAcceptedResponseV1>(accepted.Value);
            Assert.Equal(escrowId, response.EscrowId);
            service.Verify(
                s => s.RequestEscrowReleaseAsync("bk-1", Caller, "proof.jpg"),
                Times.Once);
            service.Verify(
                s => s.SubmitProofAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<decimal>()),
                Times.Never);
            Assert.NotNull(enqueued);
            Assert.Equal(PaymentOperation.ReleaseEscrow, enqueued.Operation);
            Assert.Equal(enqueued.SagaId, response.SagaId);
            Assert.Equal(
                $"/api/booking/payment-status/{enqueued.SagaId:D}",
                response.StatusUrl);
            Assert.Equal("admin-custody", enqueued.PayerUserId);
            Assert.Equal(OwnerUsername, enqueued.PayeeUserId);
            Assert.Equal(100m, enqueued.Amount);
            Assert.Null(enqueued.PaymentMethodToken);
            notifications.Verify(n => n.PublishAsync(It.IsAny<object>()), Times.Never);
        }

        [Fact]
        public async Task Cancel_FundedBooking_EnqueuesRefundAndReturns202()
        {
            var service = new Mock<IBookingService>();
            var notifications = new Mock<INotificationProducer>();
            var sagaState = new Mock<ISagaStateService>();
            var escrowId = Guid.NewGuid();
            var booking = new Booking
            {
                Id = "bk-1",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = OwnerUsername,
                RequesterUsername = Caller,
                Status = Booking.StatusAccepted,
                EscrowId = escrowId,
                EscrowStatus = Payment.Contracts.V1.EscrowStatus.Funded,
                AgreedAmount = 100m,
                AgreedCurrency = "USD",
                RefundRequestedAt = DateTime.UtcNow
            };
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);
            service.Setup(s => s.RequestCancellationAsync("bk-1", Caller))
                .ReturnsAsync(booking);
            PaymentRequestedV1? enqueued = null;
            sagaState.Setup(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<PaymentRequestedV1, string?, CancellationToken>(
                    (request, _, _) => enqueued = request)
                .ReturnsAsync((PaymentRequestedV1 request, string? _, CancellationToken _) =>
                    new SagaState { SagaId = request.SagaId });

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                notifications,
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());
            var result = await controller.Cancel("bk-1");

            var accepted = Assert.IsType<AcceptedResult>(result);
            var response = Assert.IsType<PaymentAcceptedResponseV1>(accepted.Value);
            Assert.Equal(escrowId, response.EscrowId);
            Assert.NotNull(enqueued);
            Assert.Equal(PaymentOperation.RefundEscrow, enqueued.Operation);
            Assert.Equal(enqueued.SagaId, response.SagaId);
            Assert.Equal(
                $"/api/booking/payment-status/{enqueued.SagaId:D}",
                response.StatusUrl);
            Assert.Equal("admin-custody", enqueued.PayerUserId);
            Assert.Equal(Caller, enqueued.PayeeUserId);
            Assert.Equal(OwnerUsername, enqueued.TaskMasterUserId);
            Assert.Null(enqueued.PaymentMethodToken);
            notifications.Verify(n => n.PublishAsync(It.IsAny<object>()), Times.Never);
        }

        [Fact]
        public async Task SubmitProof_WhenReleaseSagaIsActive_Returns409WithoutChangingProof()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            service.Setup(s => s.GetByIdAsync("bk-1"))
                .ReturnsAsync(new Booking { Id = "bk-1", EscrowId = Guid.NewGuid() });
            sagaState.Setup(s => s.GetLatestByBookingIdAsync("bk-1"))
                .ReturnsAsync(new SagaState
                {
                    Status = SagaState.StatusStarted,
                    Operation = PaymentOperation.ReleaseEscrow
                });
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.SubmitProof(
                "bk-1",
                new BookingController.SubmitProofDto
                {
                    ProofFileUrl = "proof.jpg"
                });

            Assert.IsType<ConflictObjectResult>(result);
            service.Verify(s => s.RequestEscrowReleaseAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task Cancel_WhenRefundSagaIsActive_Returns409WithoutChangingBooking()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            service.Setup(s => s.GetByIdAsync("bk-1"))
                .ReturnsAsync(new Booking
                {
                    Id = "bk-1",
                    EscrowStatus = EscrowStatus.Funded
                });
            sagaState.Setup(s => s.GetLatestByBookingIdAsync("bk-1"))
                .ReturnsAsync(new SagaState
                {
                    Status = SagaState.StatusStarted,
                    Operation = PaymentOperation.RefundEscrow
                });
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Cancel("bk-1");

            Assert.IsType<ConflictObjectResult>(result);
            service.Verify(s => s.RequestCancellationAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task SubmitProof_WhenOutboxPersistenceFails_ProofMutationRemainsCompleted()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var escrowId = Guid.NewGuid();
            var updated = new Booking
            {
                Id = "bk-1",
                TaskMasterUsername = OwnerUsername,
                RequesterUsername = Caller,
                EscrowId = escrowId,
                EscrowStatus = EscrowStatus.Funded,
                Status = Booking.StatusImplemented,
                AgreedAmount = 100m,
                AgreedCurrency = "USD",
                ProofFileUrl = "proof.jpg",
                InvoiceAmount = 100m,
                ReleaseRequestedAt = DateTime.UtcNow
            };
            service.Setup(s => s.GetByIdAsync("bk-1"))
                .ReturnsAsync(new Booking { Id = "bk-1", EscrowId = escrowId });
            service.Setup(s => s.RequestEscrowReleaseAsync(
                    "bk-1",
                    Caller,
                    "proof.jpg"))
                .ReturnsAsync(updated);
            sagaState.Setup(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SagaOutboxPersistenceException(
                    "Mongo unavailable",
                    new TimeoutException()));
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            await Assert.ThrowsAsync<SagaOutboxPersistenceException>(
                () => controller.SubmitProof(
                    "bk-1",
                    new BookingController.SubmitProofDto
                    {
                        ProofFileUrl = "proof.jpg"
                    }));

            service.Verify(s => s.RequestEscrowReleaseAsync(
                    "bk-1",
                    Caller,
                    "proof.jpg"),
                Times.Once);
        }

        [Fact]
        public async Task SubmitProof_AfterOutboxFailure_RetryEnqueuesPersistedRelease()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var escrowId = Guid.NewGuid();
            var updated = new Booking
            {
                Id = "bk-1",
                TaskMasterUsername = OwnerUsername,
                RequesterUsername = Caller,
                EscrowId = escrowId,
                EscrowStatus = EscrowStatus.Funded,
                Status = Booking.StatusImplemented,
                AgreedAmount = 100m,
                AgreedCurrency = "USD",
                ProofFileUrl = "proof.jpg",
                InvoiceAmount = 100m,
                ReleaseRequestedAt = DateTime.UtcNow
            };
            service.Setup(s => s.GetByIdAsync("bk-1"))
                .ReturnsAsync(new Booking { Id = "bk-1", EscrowId = escrowId });
            service.Setup(s => s.RequestEscrowReleaseAsync(
                    "bk-1",
                    Caller,
                    "proof.jpg"))
                .ReturnsAsync(updated);
            var enqueueAttempts = 0;
            sagaState.Setup(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Returns((
                    PaymentRequestedV1 request,
                    string? _,
                    CancellationToken _) =>
                {
                    enqueueAttempts++;
                    if (enqueueAttempts == 1)
                    {
                        throw new SagaOutboxPersistenceException(
                            "Mongo unavailable",
                            new TimeoutException());
                    }

                    return Task.FromResult(new SagaState
                    {
                        SagaId = request.SagaId,
                        EscrowId = request.EscrowId,
                        BookingId = request.BookingId,
                        Operation = request.Operation,
                        Status = SagaState.StatusStarted
                    });
                });
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());
            var body = new BookingController.SubmitProofDto
            {
                ProofFileUrl = "proof.jpg"
            };

            await Assert.ThrowsAsync<SagaOutboxPersistenceException>(
                () => controller.SubmitProof("bk-1", body));
            var retry = await controller.SubmitProof("bk-1", body);

            Assert.IsType<AcceptedResult>(retry);
            service.Verify(s => s.RequestEscrowReleaseAsync(
                    "bk-1",
                    Caller,
                    "proof.jpg"),
                Times.Exactly(2));
            sagaState.Verify(s => s.EnqueueAsync(
                    It.Is<PaymentRequestedV1>(request =>
                        request.Operation == PaymentOperation.ReleaseEscrow),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }

        [Fact]
        public async Task SubmitProof_TerminalEscrow_Returns409WithoutEnqueuing()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            service.Setup(s => s.GetByIdAsync("bk-1"))
                .ReturnsAsync(new Booking
                {
                    Id = "bk-1",
                    EscrowId = Guid.NewGuid(),
                    EscrowStatus = EscrowStatus.Released
                });
            service.Setup(s => s.RequestEscrowReleaseAsync(
                    "bk-1",
                    Caller,
                    "proof.jpg"))
                .ThrowsAsync(new InvalidOperationException(
                    "Escrow must be FUNDED before proof can request release"));
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.SubmitProof(
                "bk-1",
                new BookingController.SubmitProofDto
                {
                    ProofFileUrl = "proof.jpg"
                });

            Assert.IsType<ConflictObjectResult>(result);
            sagaState.Verify(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Cancel_TerminalEscrow_Returns409WithoutEnqueuing()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            service.Setup(s => s.GetByIdAsync("bk-1"))
                .ReturnsAsync(new Booking
                {
                    Id = "bk-1",
                    EscrowId = Guid.NewGuid(),
                    EscrowStatus = EscrowStatus.Refunded,
                    Status = Booking.StatusCancelled
                });
            service.Setup(s => s.RequestCancellationAsync("bk-1", Caller))
                .ThrowsAsync(new InvalidOperationException(
                    "A booking can be cancelled only before work starts"));
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Cancel("bk-1");

            Assert.IsType<ConflictObjectResult>(result);
            sagaState.Verify(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Pay_EscrowBooking_Returns409WithoutCallingLegacyPayment()
        {
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(new Booking
            {
                Id = "bk-1",
                RequesterUsername = Caller,
                Status = Booking.StatusImplemented,
                EscrowId = Guid.NewGuid(),
                InvoiceAmount = 100m
            });

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                paymentClient: paymentClient);
            var result = await controller.Pay("bk-1", ValidPayDto());

            Assert.IsType<ConflictObjectResult>(result);
            paymentClient.Verify(c => c.ProcessPaymentAsync(
                    It.IsAny<CreditCardInfo>(),
                    It.IsAny<decimal>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Never);
        }

        // ---- Pay ----

        private static Booking AcceptedBooking(Guid? escrowId = null) => new()
        {
            Id = "bk-1",
            TaskMasterId = TaskMasterId,
            TaskMasterUsername = OwnerUsername,
            RequesterUsername = Caller,
            SlotStart = FutureSlot(),
            DurationHours = 1,
            Status = Booking.StatusAccepted,
            AgreedAmount = 100m,
            AgreedCurrency = "USD",
            EscrowId = escrowId,
            EscrowStatus = escrowId.HasValue ? EscrowStatus.Pending : null
        };

        private static IConfiguration AsyncPaymentConfiguration() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AsyncPaymentsEnabled"] = "true",
                    ["Escrow:CustodyUserId"] = "admin-custody"
                })
                .Build();

        private static BookingController.PayDto ValidTokenPayDto() => new()
        {
            PaymentMethodToken = "pmt_opaque-token"
        };

        [Fact]
        public async Task Pay_AsyncEnabled_AttachesEscrowEnqueuesFundingAndReturns202()
        {
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = new Mock<ISagaStateService>();
            var accepted = AcceptedBooking();
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(accepted);
            service.Setup(s => s.AttachEscrowAsync("bk-1", Caller, It.IsAny<Guid>()))
                .ReturnsAsync((string _, string _, Guid escrowId) =>
                {
                    accepted.EscrowId = escrowId;
                    accepted.EscrowStatus = EscrowStatus.Pending;
                    return accepted;
                });

            PaymentRequestedV1? enqueued = null;
            sagaState.Setup(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<PaymentRequestedV1, string?, CancellationToken>(
                    (request, _, _) => enqueued = request)
                .ReturnsAsync((PaymentRequestedV1 request, string? _, CancellationToken _) =>
                    new SagaState
                    {
                        SagaId = request.SagaId,
                        EscrowId = request.EscrowId,
                        BookingId = request.BookingId,
                        Operation = request.Operation
                    });

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                paymentClient: paymentClient,
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Pay("bk-1", ValidTokenPayDto());

            var acceptedResult = Assert.IsType<AcceptedResult>(result);
            var response = Assert.IsType<PaymentAcceptedResponseV1>(acceptedResult.Value);
            Assert.NotEqual(Guid.Empty, response.SagaId);
            Assert.Equal(accepted.EscrowId, response.EscrowId);
            Assert.Equal(PaymentAcceptedResponseV1.PendingStatus, response.Status);
            Assert.Equal(
                $"/api/booking/payment-status/{response.SagaId:D}",
                response.StatusUrl);
            Assert.Equal(response.StatusUrl, acceptedResult.Location);

            Assert.NotNull(enqueued);
            Assert.Equal(response.SagaId, enqueued.SagaId);
            Assert.Equal(response.EscrowId, enqueued.EscrowId);
            Assert.Equal("bk-1", enqueued.BookingId);
            Assert.Equal(PaymentOperation.FundEscrow, enqueued.Operation);
            Assert.Equal(100m, enqueued.Amount);
            Assert.Equal("USD", enqueued.Currency);
            Assert.Equal(Caller, enqueued.PayerUserId);
            Assert.Equal("admin-custody", enqueued.PayeeUserId);
            Assert.Equal("pmt_opaque-token", enqueued.PaymentMethodToken);
            paymentClient.Verify(c => c.ProcessPaymentAsync(
                    It.IsAny<CreditCardInfo>(),
                    It.IsAny<decimal>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task Pay_AsyncEnabled_RetryReusesPendingEscrow()
        {
            var escrowId = Guid.NewGuid();
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            service.Setup(s => s.GetByIdAsync("bk-1"))
                .ReturnsAsync(AcceptedBooking(escrowId));
            sagaState.Setup(s => s.EnqueueAsync(
                    It.Is<PaymentRequestedV1>(request => request.EscrowId == escrowId),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((PaymentRequestedV1 request, string? _, CancellationToken _) =>
                    new SagaState { SagaId = request.SagaId });

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Pay("bk-1", ValidTokenPayDto());

            Assert.IsType<AcceptedResult>(result);
            service.Verify(
                s => s.AttachEscrowAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()),
                Times.Never);
        }

        [Fact]
        public async Task Pay_AsyncEnabled_WithoutToken_Returns400()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Pay("bk-1", new BookingController.PayDto());

            Assert.IsType<BadRequestObjectResult>(result);
            service.Verify(s => s.GetByIdAsync(It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Theory]
        [InlineData(Booking.StatusPending)]
        [InlineData(Booking.StatusInProgress)]
        [InlineData(Booking.StatusImplemented)]
        [InlineData(Booking.StatusCompleted)]
        public async Task Pay_AsyncEnabled_WhenBookingIsNotAccepted_Returns409(
            string status)
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var booking = AcceptedBooking();
            booking.Status = status;
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Pay("bk-1", ValidTokenPayDto());

            Assert.IsType<ConflictObjectResult>(result);
            service.Verify(s => s.AttachEscrowAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()),
                Times.Never);
            sagaState.Verify(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Pay_AsyncEnabled_WhenFundingSagaIsActive_Returns409()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(AcceptedBooking());
            sagaState.Setup(s => s.GetLatestByBookingIdAsync("bk-1"))
                .ReturnsAsync(new SagaState
                {
                    BookingId = "bk-1",
                    Status = SagaState.StatusStarted,
                    Operation = PaymentOperation.FundEscrow
                });

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Pay("bk-1", ValidTokenPayDto());

            Assert.IsType<ConflictObjectResult>(result);
            service.Verify(s => s.AttachEscrowAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()),
                Times.Never);
            sagaState.Verify(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Pay_AsyncEnabled_WhenCallerIsNotRequester_Returns403()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var booking = AcceptedBooking();
            booking.RequesterUsername = "carol";
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Pay("bk-1", ValidTokenPayDto());

            Assert.IsType<ForbidResult>(result);
            sagaState.Verify(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Pay_AsyncEnabled_WithoutFixedPrice_Returns409()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var booking = AcceptedBooking();
            booking.AgreedAmount = null;
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Pay("bk-1", ValidTokenPayDto());

            Assert.IsType<ConflictObjectResult>(result);
            service.Verify(s => s.AttachEscrowAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>()),
                Times.Never);
            sagaState.Verify(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Pay_AsyncEnabled_WhenEscrowAlreadyFunded_Returns409()
        {
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            var booking = AcceptedBooking(Guid.NewGuid());
            booking.EscrowStatus = EscrowStatus.Funded;
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Pay("bk-1", ValidTokenPayDto());

            Assert.IsType<ConflictObjectResult>(result);
            sagaState.Verify(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task Pay_AsyncEnabled_WhenConcurrentEnqueueWins_Returns409()
        {
            var escrowId = Guid.NewGuid();
            var service = new Mock<IBookingService>();
            var sagaState = new Mock<ISagaStateService>();
            service.Setup(s => s.GetByIdAsync("bk-1"))
                .ReturnsAsync(AcceptedBooking(escrowId));
            sagaState.Setup(s => s.EnqueueAsync(
                    It.IsAny<PaymentRequestedV1>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new calendar_service.Services.ActivePaymentSagaException(
                    "bk-1",
                    PaymentOperation.FundEscrow));

            var controller = BuildController(
                service,
                new Mock<ITaskMasterApiClient>(),
                new Mock<INotificationProducer>(),
                sagaStateService: sagaState,
                configuration: AsyncPaymentConfiguration());

            var result = await controller.Pay("bk-1", ValidTokenPayDto());

            Assert.IsType<ConflictObjectResult>(result);
        }

        private static Booking ImplementedBooking(decimal invoiceAmount = 100m) => new()
        {
            Id = "bk-1",
            TaskMasterId = TaskMasterId,
            TaskMasterUsername = OwnerUsername,
            RequesterUsername = Caller,
            SlotStart = FutureSlot(),
            DurationHours = 1,
            Status = Booking.StatusImplemented,
            InvoiceAmount = invoiceAmount
        };

        private static BookingController.PayDto ValidPayDto() => new()
        {
            CardNumber = "4111111111111111",
            ExpiryDate = "12/30",
            CVV = "123",
            OwnerName = Caller
        };

        [Fact]
        public async Task Pay_HappyPath_StartsAndCompletesSaga_Returns200()
        {
            var service = new Mock<IBookingService>();
            var notifications = new Mock<INotificationProducer>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = DefaultSagaStateServiceMock();

            var booking = ImplementedBooking();
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);

            var sagaId = Guid.Empty;
            sagaState.Setup(s => s.StartAsync("bk-1", It.IsAny<Guid>(), 100m))
                .ReturnsAsync((string bookingId, Guid id, decimal amount) =>
                {
                    sagaId = id;
                    return new SagaState { SagaId = id, BookingId = bookingId, RequestedAmount = amount };
                });

            paymentClient
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = PaymentTransactionResult.StatusApproved });

            var completed = new Booking
            {
                Id = "bk-1",
                TaskMasterId = TaskMasterId,
                TaskMasterUsername = OwnerUsername,
                RequesterUsername = Caller,
                SlotStart = booking.SlotStart,
                DurationHours = 1,
                Status = Booking.StatusCompleted,
                InvoiceAmount = 100m,
                PaymentTransactionId = "txn-1"
            };
            service.Setup(s => s.CompletePaymentAsync("bk-1", Caller, "txn-1")).ReturnsAsync(completed);

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), notifications,
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(completed, ok.Value);
            // The sagaId started before the payment call must be the same one completed afterwards.
            paymentClient.Verify(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), sagaId, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            sagaState.Verify(s => s.CompleteAsync(sagaId, "txn-1"), Times.Once);
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Pay_WhenSimulatePostChargeCrashEnabled_ThrowsAndLeavesSagaStarted()
        {
            // Verifies the Faults:SimulatePostChargeCrash test hook: after the charge succeeds,
            // the request throws instead of ever calling CompletePaymentAsync/CompleteAsync —
            // simulating a real process crash for exercising the reconciliation job manually.
            var service = new Mock<IBookingService>();
            var notifications = new Mock<INotificationProducer>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = DefaultSagaStateServiceMock();

            var booking = ImplementedBooking();
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);
            paymentClient
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = PaymentTransactionResult.StatusApproved });

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Faults:SimulatePostChargeCrash"] = "true" })
                .Build();

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), notifications,
                paymentClient: paymentClient, sagaStateService: sagaState, configuration: configuration);

            await Assert.ThrowsAsync<BookingController.SimulatedPostChargeCrashException>(() => ctrl.Pay("bk-1", ValidPayDto()));

            service.Verify(s => s.CompletePaymentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Pay_WhenPaymentAlreadyStartedForBooking_Returns409_AndNeverStartsNewSaga()
        {
            // Server-side backstop for the frontend's "payment is being processed" block: even
            // if the UI check is stale/bypassed (e.g. two tabs open), a new /pay attempt must be
            // rejected while a previous saga for the same booking is still ambiguously STARTED,
            // so we never mint a second concurrent charge attempt for the same booking.
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = new Mock<ISagaStateService>(MockBehavior.Strict);

            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(ImplementedBooking());
            sagaState.Setup(s => s.GetLatestByBookingIdAsync("bk-1"))
                .ReturnsAsync(new SagaState { BookingId = "bk-1", Status = SagaState.StatusStarted });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(),
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("already being processed", conflict.Value!.ToString());
            paymentClient.Verify(c => c.ProcessPaymentAsync(
                It.IsAny<CreditCardInfo>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.StartAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<decimal>()), Times.Never);
        }

        [Fact]
        public async Task Pay_WhenPreviousSagaForBookingIsResolved_ProceedsNormally()
        {
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = DefaultSagaStateServiceMock();
            sagaState.Setup(s => s.GetLatestByBookingIdAsync("bk-1"))
                .ReturnsAsync(new SagaState { BookingId = "bk-1", Status = SagaState.StatusFailed });

            var booking = ImplementedBooking();
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);
            paymentClient
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = PaymentTransactionResult.StatusApproved });
            service.Setup(s => s.CompletePaymentAsync("bk-1", Caller, "txn-1")).ReturnsAsync(new Booking { Id = "bk-1", Status = Booking.StatusCompleted });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(),
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            Assert.IsType<OkObjectResult>(result);
        }

        [Fact]
        public async Task Pay_WhenSagaStateStoreUnavailable_Returns503_AndNeverAttemptsPayment()
        {
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = new Mock<ISagaStateService>();

            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(ImplementedBooking());
            sagaState.Setup(s => s.StartAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<decimal>()))
                .ThrowsAsync(new TimeoutException("Mongo unreachable"));

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(),
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, objResult.StatusCode);
            // No card should ever be charged if the saga row couldn't be durably recorded first.
            paymentClient.Verify(c => c.ProcessPaymentAsync(
                It.IsAny<CreditCardInfo>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Pay_WhenPaymentServiceUnreachable_LeavesSagaStarted_Returns502WithClearMessage()
        {
            // Regression coverage: previously this path called FailAsync immediately, which (a)
            // meant the reconciliation job never saw the saga (it was never left STARTED), and
            // (b) risked mislabeling a charge that actually succeeded server-side (response just
            // never arrived) as "not charged". Neither is safe — the saga must stay STARTED so
            // SagaReconciliationWorker can resolve it authoritatively once payment-service is
            // reachable again.
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = DefaultSagaStateServiceMock();

            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(ImplementedBooking());
            paymentClient
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new PaymentServiceUnavailableException("payment-service unreachable"));

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(),
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status502BadGateway, objResult.StatusCode);
            var error = Assert.IsAssignableFrom<string>(objResult.Value!.GetType().GetProperty("error")!.GetValue(objResult.Value));
            Assert.Contains("do not retry", error, StringComparison.OrdinalIgnoreCase);
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Pay_WhenPaymentDeclined_FailsSaga_Returns402()
        {
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = DefaultSagaStateServiceMock();

            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(ImplementedBooking());
            paymentClient
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = "DECLINED" });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(),
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(402, objResult.StatusCode);
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.Is<string>(r => r.Contains("declined", StringComparison.OrdinalIgnoreCase))), Times.Once);
        }

        [Fact]
        public async Task Pay_WhenPaymentDeclinedWithReason_IncludesReasonInErrorMessage()
        {
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = DefaultSagaStateServiceMock();

            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(ImplementedBooking());
            paymentClient
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = "DECLINED", DeclineReason = "Insufficient balance (your balance is 50.00 USD, but the charge is 100.00 USD)" });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(),
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(402, objResult.StatusCode);
            var error = objResult.Value?.GetType().GetProperty("error")?.GetValue(objResult.Value) as string;
            Assert.Contains("Insufficient balance", error);
            Assert.Contains("50.00", error);
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.Is<string>(r => r.Contains("Insufficient balance"))), Times.Once);
        }

        [Fact]
        public async Task Pay_WhenAmountMismatch_FailsSaga_Returns402()
        {
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = DefaultSagaStateServiceMock();

            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(ImplementedBooking());
            paymentClient
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 999m, Status = PaymentTransactionResult.StatusApproved });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(),
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(402, objResult.StatusCode);
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.Is<string>(r => r.Contains("match", StringComparison.OrdinalIgnoreCase))), Times.Once);
        }

        [Fact]
        public async Task Pay_WhenCompletePaymentThrowsAfterChargeSucceeded_LeavesSagaStarted_Returns409()
        {
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = DefaultSagaStateServiceMock();

            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(ImplementedBooking());
            paymentClient
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = PaymentTransactionResult.StatusApproved });
            service.Setup(s => s.CompletePaymentAsync("bk-1", Caller, "txn-1"))
                .ThrowsAsync(new InvalidOperationException("Booking is COMPLETED and cannot be paid"));

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(),
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            Assert.IsType<ConflictObjectResult>(result);
            // The charge already succeeded, so the saga must NOT be marked FAILED — it's left
            // STARTED for the reconciliation job to pick up and finish (see PAYMENT_SAGA_SPEC.md).
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            sagaState.Verify(s => s.CompleteAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        // ---- Listing & lookup ----

        [Fact]
        public async Task ListIncoming_PassesCallerAndStatus()
        {
            var service = new Mock<IBookingService>();
            var data = new List<Booking> { new() { Id = "bk-1" } };
            service.Setup(s => s.ListIncomingForTaskMasterAsync(Caller, "PENDING")).ReturnsAsync(data);

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>());
            var result = await ctrl.ListIncoming("PENDING");

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(data, ok.Value);
        }

        [Fact]
        public async Task ListOutgoing_PassesCallerAndStatus()
        {
            var service = new Mock<IBookingService>();
            var data = new List<Booking> { new() { Id = "bk-1" } };
            service.Setup(s => s.ListOutgoingForRequesterAsync(Caller, null)).ReturnsAsync(data);

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>());
            var result = await ctrl.ListOutgoing(null);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(data, ok.Value);
        }

        [Fact]
        public async Task GetTimetable_OwnerSeesAllStatuses()
        {
            var service = new Mock<IBookingService>();
            var tmClient = new Mock<ITaskMasterApiClient>();
            tmClient.Setup(c => c.GetByIdAsync(TaskMasterId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new TaskMasterLookup { Id = TaskMasterId, OwnerUsername = Caller });

            var data = new List<Booking> { new() { Id = "bk-1" } };
            // Owner flag must be true; admin flag false.
            service.Setup(s => s.GetTimetableAsync(TaskMasterId, Caller, false, true)).ReturnsAsync(data);

            var ctrl = BuildController(service, tmClient, new Mock<INotificationProducer>());
            var result = await ctrl.GetTimetable(TaskMasterId);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(data, ok.Value);
            service.VerifyAll();
        }

        [Fact]
        public async Task GetTimetable_AdminSeesAllStatuses_EvenWhenNotOwner()
        {
            var service = new Mock<IBookingService>();
            var tmClient = new Mock<ITaskMasterApiClient>();
            tmClient.Setup(c => c.GetByIdAsync(TaskMasterId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new TaskMasterLookup { Id = TaskMasterId, OwnerUsername = OwnerUsername });

            var data = new List<Booking> { new() { Id = "bk-1" } };
            service.Setup(s => s.GetTimetableAsync(TaskMasterId, Caller, true, false)).ReturnsAsync(data);

            var ctrl = BuildController(service, tmClient, new Mock<INotificationProducer>(), isAdmin: true);
            var result = await ctrl.GetTimetable(TaskMasterId);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(data, ok.Value);
            service.VerifyAll();
        }

        [Fact]
        public async Task Get_WhenServiceReturnsNull_Returns404()
        {
            var service = new Mock<IBookingService>();
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync((Booking?)null);

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>());
            var result = await ctrl.Get("bk-1");

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Get_HappyPath_Returns200()
        {
            var service = new Mock<IBookingService>();
            var booking = new Booking { Id = "bk-1" };
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>());
            var result = await ctrl.Get("bk-1");

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(booking, ok.Value);
        }

        [Fact]
        public async Task Get_BookingImplementedWithStartedSaga_SetsPaymentPendingTrue()
        {
            // Reproduces the scenario the frontend needs to survive a page reload for: a saga
            // is ambiguously STARTED (e.g. payment-service was unreachable mid-charge) so the
            // booking is still IMPLEMENTED; GET must flag PaymentPending so the frontend can
            // block a duplicate /pay attempt even after the user closes and reopens the browser.
            var service = new Mock<IBookingService>();
            var booking = new Booking { Id = "bk-1", Status = Booking.StatusImplemented, InvoiceAmount = 50m };
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);
            var sagaState = new Mock<ISagaStateService>();
            sagaState.Setup(s => s.GetLatestByBookingIdAsync("bk-1"))
                .ReturnsAsync(new SagaState { BookingId = "bk-1", Status = SagaState.StatusStarted });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(), sagaStateService: sagaState);
            var result = await ctrl.Get("bk-1");

            var ok = Assert.IsType<OkObjectResult>(result);
            var returned = Assert.IsType<Booking>(ok.Value);
            Assert.True(returned.PaymentPending);
        }

        [Fact]
        public async Task Get_BookingImplementedWithResolvedSaga_LeavesPaymentPendingFalse()
        {
            var service = new Mock<IBookingService>();
            var booking = new Booking { Id = "bk-1", Status = Booking.StatusImplemented, InvoiceAmount = 50m };
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);
            var sagaState = new Mock<ISagaStateService>();
            sagaState.Setup(s => s.GetLatestByBookingIdAsync("bk-1"))
                .ReturnsAsync(new SagaState { BookingId = "bk-1", Status = SagaState.StatusFailed });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(), sagaStateService: sagaState);
            var result = await ctrl.Get("bk-1");

            var ok = Assert.IsType<OkObjectResult>(result);
            var returned = Assert.IsType<Booking>(ok.Value);
            Assert.False(returned.PaymentPending);
        }

        [Fact]
        public async Task Get_TerminalBooking_StillProjectsLatestSagaForReloadedFrontend()
        {
            var service = new Mock<IBookingService>();
            var booking = new Booking { Id = "bk-1", Status = Booking.StatusCompleted };
            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(booking);
            var sagaState = new Mock<ISagaStateService>();
            sagaState.Setup(s => s.GetLatestByBookingIdAsync("bk-1"))
                .ReturnsAsync(new SagaState
                {
                    SagaId = Guid.NewGuid(),
                    BookingId = "bk-1",
                    EscrowId = Guid.NewGuid(),
                    Status = SagaState.StatusCompleted,
                    Operation = PaymentOperation.ReleaseEscrow
                });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(), sagaStateService: sagaState);
            var result = await ctrl.Get("bk-1");

            var ok = Assert.IsType<OkObjectResult>(result);
            var returned = Assert.IsType<Booking>(ok.Value);
            Assert.Equal(SagaState.StatusCompleted, returned.LatestPaymentStatus);
            Assert.Equal(PaymentOperation.ReleaseEscrow, returned.LatestPaymentOperation);
        }
    }
}
