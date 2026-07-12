using System.Security.Claims;
using calendar_service.Controllers;
using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services.Clients;
using calendar_service.Services.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

        // ---- Pay ----

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
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
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
            paymentClient.Verify(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), sagaId), Times.Once);
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
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
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
                It.IsAny<CreditCardInfo>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>()), Times.Never);
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
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
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
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
                .ReturnsAsync(new PaymentTransactionResult { Id = "txn-1", Amount = 100m, Status = "DECLINED" });

            var ctrl = BuildController(service, new Mock<ITaskMasterApiClient>(), new Mock<INotificationProducer>(),
                paymentClient: paymentClient, sagaStateService: sagaState);
            var result = await ctrl.Pay("bk-1", ValidPayDto());

            var objResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(402, objResult.StatusCode);
            sagaState.Verify(s => s.FailAsync(It.IsAny<Guid>(), It.Is<string>(r => r.Contains("declined", StringComparison.OrdinalIgnoreCase))), Times.Once);
        }

        [Fact]
        public async Task Pay_WhenAmountMismatch_FailsSaga_Returns402()
        {
            var service = new Mock<IBookingService>();
            var paymentClient = new Mock<IPaymentApiClient>();
            var sagaState = DefaultSagaStateServiceMock();

            service.Setup(s => s.GetByIdAsync("bk-1")).ReturnsAsync(ImplementedBooking());
            paymentClient
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
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
                .Setup(c => c.ProcessPaymentAsync(It.IsAny<CreditCardInfo>(), 100m, It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
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
    }
}
