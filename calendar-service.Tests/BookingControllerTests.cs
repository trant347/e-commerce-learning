using System.Security.Claims;
using calendar_service.Controllers;
using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services.Clients;
using calendar_service.Services.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
            string? bearer = "test-token")
        {
            var controller = new BookingController(
                service.Object,
                taskMasterClient.Object,
                notifications.Object,
                NullLogger<BookingController>.Instance);

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
            service.Setup(s => s.CreateAsync(TaskMasterId, OwnerUsername, Caller, slot, 2, "hi"))
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
                        It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>()))
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
