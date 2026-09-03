using System.Security.Claims;
using calendar_service.Contracts;
using calendar_service.Filters;
using calendar_service.Model;
using calendar_service.Services.Clients;
using calendar_service.Services.Contracts;
using Microsoft.AspNetCore.Mvc;
using Payment.Contracts.V1;

namespace calendar_service.Controllers
{
    /// <summary>
    /// HTTP API for creating, viewing, and progressing bookings through their lifecycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If an endpoint primarily manages another resource or requires substantial orchestration,
    /// add it to that resource's controller/service instead of expanding this controller.
    /// </para>
    /// </remarks>
    [ApiController]
    [Route("api/booking")]
    [TypeFilter(typeof(BookingExceptionFilter))]
    public class BookingController : ControllerBase
    {
        private readonly IBookingService _service;
        private readonly ITaskMasterApiClient _taskMasterClient;
        private readonly ISagaStateService _sagaStateService;
        private readonly IEscrowPaymentService _escrow;
        private readonly IBookingNotifier _notifier;

        public BookingController(
            IBookingService service,
            ITaskMasterApiClient taskMasterClient,
            ISagaStateService sagaStateService,
            IEscrowPaymentService escrow,
            IBookingNotifier notifier)
        {
            _service = service;
            _taskMasterClient = taskMasterClient;
            _sagaStateService = sagaStateService;
            _escrow = escrow;
            _notifier = notifier;
        }

        public class CreateBookingDto
        {
            public string TaskMasterId { get; set; } = string.Empty;
            public DateTime SlotStart { get; set; }
            public int DurationHours { get; set; } = 1;
            public string? Message { get; set; }
            public decimal? OfferedRatePerHour { get; set; }
        }

        public class RespondDto
        {
            public string? Message { get; set; }
        }

        public class SubmitProofDto
        {
            public string ProofFileUrl { get; set; } = string.Empty;
        }

        public class PayDto
        {
            public string PaymentMethodToken { get; set; } = string.Empty;
        }

        /// <summary>
        /// GET <c>/api/booking/taskmasters/{taskMasterId}/timetable</c> — returns the TaskMaster's
        /// schedule for display on the public timetable.
        /// </summary>
        /// <remarks>
        /// Visibility is role-aware: admins and the TaskMaster owner see all ACCEPTED, PENDING
        /// and DECLINED bookings (including past slots); other authenticated callers see ACCEPTED
        /// slots plus their own PENDING requests, and past slots are hidden.
        /// </remarks>
        /// <response code="200">A list of bookings ordered by SlotStart ascending.</response>
        [HttpGet("taskmasters/{taskMasterId}/timetable")]
        public async Task<IActionResult> GetTimetable(string taskMasterId)
        {
            var caller = CurrentUsername();
            var isAdmin = IsAdmin();

            var tm = await _taskMasterClient.GetByIdAsync(taskMasterId, BearerFromRequest(), HttpContext.RequestAborted);
            var callerIsOwner = !string.IsNullOrEmpty(caller)
                && !string.IsNullOrEmpty(tm?.OwnerUsername)
                && string.Equals(caller, tm!.OwnerUsername, StringComparison.OrdinalIgnoreCase);

            var slots = await _service.GetTimetableAsync(taskMasterId, caller, isAdmin, callerIsOwner);
            return Ok(BookingResponse.FromMany(slots));
        }

        /// <summary>
        /// POST <c>/api/booking</c> — submits a new PENDING booking request from the caller
        /// against the given TaskMaster.
        /// </summary>
        /// <remarks>
        /// Resolves the TaskMaster owner via product-service, then delegates to
        /// <see cref="IBookingService.CreateAsync"/> for validation and overlap checks.
        /// On success, publishes a <c>BOOKING_REQUEST_SUBMITTED</c> notification to the owner.
        /// </remarks>
        /// <response code="200">Booking created in PENDING state.</response>
        /// <response code="400">Missing taskMasterId or TaskMaster has no owner.</response>
        /// <response code="401">No authenticated caller.</response>
        /// <response code="404">TaskMaster not found.</response>
        /// <response code="409">Validation failed (past slot, bad duration, self-book, or overlap with another booking).</response>
        [HttpPost("")]
        public async Task<IActionResult> Create([FromBody] CreateBookingDto dto)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();
            if (dto == null || string.IsNullOrEmpty(dto.TaskMasterId)) return BadRequest("taskMasterId is required");

            var tm = await _taskMasterClient.GetByIdAsync(dto.TaskMasterId, BearerFromRequest(), HttpContext.RequestAborted);
            if (tm == null) return NotFound("TaskMaster not found");
            if (string.IsNullOrEmpty(tm.OwnerUsername))
            {
                return BadRequest("This TaskMaster does not have an owner and cannot be booked");
            }

            var created = await _service.CreateAsync(
                dto.TaskMasterId, tm.OwnerUsername!, caller, dto.SlotStart, dto.DurationHours, dto.Message,
                dto.OfferedRatePerHour);

            await _notifier.RequestSubmittedAsync(created, tm.OwnerUsername!);
            return Ok(BookingResponse.From(created));
        }

        /// <summary>
        /// GET <c>/api/booking/incoming?status=...</c> — lists booking requests addressed to the
        /// caller (i.e. bookings against TaskMasters the caller owns). Optional status filter.
        /// </summary>
        /// <response code="200">Bookings ordered by CreatedAt descending.</response>
        /// <response code="401">No authenticated caller.</response>
        [HttpGet("incoming")]
        public async Task<IActionResult> ListIncoming([FromQuery] string? status)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();
            var list = await _service.ListIncomingForTaskMasterAsync(caller, status);
            return Ok(BookingResponse.FromMany(list));
        }

        /// <summary>
        /// GET <c>/api/booking/outgoing?status=...</c> — lists bookings the caller has raised
        /// against other TaskMasters. Optional status filter.
        /// </summary>
        /// <response code="200">Bookings ordered by CreatedAt descending.</response>
        /// <response code="401">No authenticated caller.</response>
        [HttpGet("outgoing")]
        public async Task<IActionResult> ListOutgoing([FromQuery] string? status)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();
            var list = await _service.ListOutgoingForRequesterAsync(caller, status);
            return Ok(BookingResponse.FromMany(list));
        }

        /// <summary>
        /// GET <c>/api/booking/{id}</c> — returns a single booking by id.
        /// </summary>
        /// <remarks>
        /// A booking exposes both parties, the agreed price, the proof file and the latest
        /// payment-saga projection, so it is readable only by the requester, the TaskMaster
        /// owner, or an administrator. Use the timetable endpoint for public slot visibility.
        /// </remarks>
        /// <response code="200">The booking.</response>
        /// <response code="401">No authenticated caller.</response>
        /// <response code="403">Caller is not a party to the booking.</response>
        /// <response code="404">No booking with that id.</response>
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(string id)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();

            var item = await _service.GetByIdAsync(id);
            if (item == null) return NotFound();
            if (!CallerMayViewBooking(item, caller)) return Forbid();

            var latestSaga = item.Id == null
                ? null
                : await _sagaStateService.GetLatestByBookingIdAsync(item.Id);
            return Ok(BookingResponse.From(item).WithLatestPayment(latestSaga));
        }

        /// <summary>
        /// Returns the durable saga status and current booking escrow projection. Only either
        /// booking party or an administrator may inspect the payment attempt.
        /// </summary>
        [HttpGet("payment-status/{sagaId:guid}")]
        public async Task<IActionResult> GetPaymentStatus(Guid sagaId)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();

            var saga = await _sagaStateService.GetBySagaIdAsync(sagaId);
            if (saga == null) return NotFound();

            var booking = await _service.GetByIdAsync(saga.BookingId);
            if (booking == null) return NotFound();

            if (!CallerMayViewBooking(booking, caller)) return Forbid();

            if (!saga.EscrowId.HasValue || string.IsNullOrWhiteSpace(saga.Operation))
            {
                return NotFound();
            }

            return Ok(new PaymentStatusResponseV1
            {
                SagaId = saga.SagaId,
                BookingId = saga.BookingId,
                EscrowId = saga.EscrowId.Value,
                Operation = saga.Operation,
                Status = saga.Status == SagaState.StatusStarted
                    ? PaymentStatusResponseV1.PendingStatus
                    : saga.Status,
                EscrowStatus = booking.EscrowStatus,
                FailureReason = saga.FailureReason,
                UpdatedAt = saga.UpdatedAt
            });
        }

        /// <summary>
        /// POST <c>/api/booking/{id}/accept</c> — accepts a PENDING booking. Only the TaskMaster
        /// owner may invoke this.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="IBookingService.AcceptAsync"/>, which atomically auto-declines
        /// every other PENDING booking whose range overlaps the accepted slot. Then publishes a
        /// <c>BOOKING_REQUEST_ACCEPTED</c> notification to the requester and one
        /// <c>BOOKING_REQUEST_DECLINED</c> notification per auto-declined sibling.
        /// </remarks>
        /// <response code="200">The accepted booking.</response>
        /// <response code="401">No authenticated caller.</response>
        /// <response code="403">Caller is not the TaskMaster owner.</response>
        /// <response code="404">No booking with that id.</response>
        /// <response code="409">Booking is not PENDING, or its range already overlaps an ACCEPTED booking.</response>
        [HttpPost("{id}/accept")]
        public async Task<IActionResult> Accept(string id, [FromBody] RespondDto? body)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();

            var result = await _service.AcceptAsync(id, caller, body?.Message);

            await _notifier.RequestAcceptedAsync(result.Accepted);
            foreach (var declined in result.AutoDeclined)
            {
                await _notifier.RequestAutoDeclinedAsync(declined);
            }
            return Ok(BookingResponse.From(result.Accepted));
        }

        /// <summary>
        /// POST <c>/api/booking/{id}/decline</c> — declines a PENDING booking. Only the TaskMaster
        /// owner may invoke this. Publishes a <c>BOOKING_REQUEST_DECLINED</c> notification on success.
        /// </summary>
        /// <response code="200">The declined booking.</response>
        /// <response code="401">No authenticated caller.</response>
        /// <response code="403">Caller is not the TaskMaster owner.</response>
        /// <response code="404">No booking with that id.</response>
        /// <response code="409">Booking is not in PENDING state.</response>
        [HttpPost("{id}/decline")]
        public async Task<IActionResult> Decline(string id, [FromBody] RespondDto? body)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();

            var updated = await _service.DeclineAsync(id, caller, body?.Message);
            if (updated == null) return NotFound();

            await _notifier.RequestDeclinedAsync(updated);
            return Ok(BookingResponse.From(updated));
        }

        /// <summary>
        /// POST <c>/api/booking/{id}/submit-proof</c> — TaskMaster owner submits proof of the
        /// completed job and requests release of the already-fixed escrow amount.
        /// </summary>
        /// <response code="202">Proof was saved and escrow release was durably enqueued.</response>
        /// <response code="400">The proof file URL is missing.</response>
        /// <response code="401">No authenticated caller.</response>
        /// <response code="403">Caller is not the TaskMaster owner.</response>
        /// <response code="404">No booking with that id.</response>
        /// <response code="409">Booking is not ACCEPTED, or a release is already in flight.</response>
        [HttpPost("{id}/submit-proof")]
        public async Task<IActionResult> SubmitProof(string id, [FromBody] SubmitProofDto body)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();
            if (body == null || string.IsNullOrWhiteSpace(body.ProofFileUrl))
            {
                return BadRequest("proofFileUrl is required");
            }

            var booking = await _service.GetByIdAsync(id);
            if (booking == null) return NotFound();
            if (!booking.EscrowId.HasValue)
            {
                return Conflict(new
                {
                    error = "Proof submission requires an escrow-funded booking"
                });
            }
            await _escrow.EnsureNoActiveOperationAsync(id, PaymentOperation.ReleaseEscrow);

            var updated = await _service.RequestEscrowReleaseAsync(id, caller, body.ProofFileUrl);
            var accepted = await _escrow.EnqueueTransferAsync(
                updated,
                PaymentOperation.ReleaseEscrow,
                HttpContext.RequestAborted);
            return Accepted(accepted.StatusUrl, accepted);
        }

        /// <summary>
        /// POST <c>/api/booking/{id}/start-work</c> — starts work only after escrow funding.
        /// </summary>
        [HttpPost("{id}/start-work")]
        public async Task<IActionResult> StartWork(string id)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();

            var updated = await _service.StartWorkAsync(id, caller);
            return Ok(BookingResponse.From(updated));
        }

        /// <summary>
        /// POST <c>/api/booking/{id}/cancel</c> — cancels before funding, or requests a refund
        /// when escrow is funded and work has not started.
        /// </summary>
        [HttpPost("{id}/cancel")]
        public async Task<IActionResult> Cancel(string id)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();

            var existing = await _service.GetByIdAsync(id);
            if (existing == null) return NotFound();
            await _escrow.EnsureNoActiveOperationAsync(id, PaymentOperation.RefundEscrow);

            var updated = await _service.RequestCancellationAsync(id, caller);

            // A funded booking can't simply be dropped: the money has to travel back through the
            // saga, so the caller gets 202 + a status URL instead of the cancelled booking.
            var refundRequested = updated.RefundRequestedAt.HasValue
                && updated.Status != Booking.StatusCancelled;
            if (!refundRequested)
            {
                await _notifier.BookingCancelledAsync(updated);
                return Ok(BookingResponse.From(updated));
            }

            var accepted = await _escrow.EnqueueTransferAsync(
                updated,
                PaymentOperation.RefundEscrow,
                HttpContext.RequestAborted);
            return Accepted(accepted.StatusUrl, accepted);
        }

        /// <summary>
        /// POST <c>/api/booking/{id}/pay</c> — the requester supplies a payment-method token for
        /// an ACCEPTED booking and receives a durable escrow-funding saga response immediately.
        /// </summary>
        /// <response code="202">Escrow funding was durably enqueued.</response>
        /// <response code="400">The payment-method token is missing.</response>
        /// <response code="401">No authenticated caller.</response>
        /// <response code="403">Caller is not the requester.</response>
        /// <response code="404">No booking with that id.</response>
        /// <response code="409">Booking is ineligible or payment is already active.</response>
        /// <response code="503">The durable saga/outbox request could not be persisted.</response>
        [HttpPost("{id}/pay")]
        public async Task<IActionResult> Pay(string id, [FromBody] PayDto body)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();
            if (body == null) return BadRequest("Payment details are required");
            if (string.IsNullOrWhiteSpace(body.PaymentMethodToken))
            {
                return BadRequest("paymentMethodToken is required");
            }

            var accepted = await _escrow.FundEscrowAsync(
                id,
                caller,
                body.PaymentMethodToken,
                HttpContext.RequestAborted);
            return Accepted(accepted.StatusUrl, accepted);
        }

        /// <summary>Returns the authenticated username from the JWT principal.</summary>
        private string? CurrentUsername() =>
            User?.FindFirst(ClaimTypes.Name)?.Value ?? User?.Identity?.Name;

        private bool IsAdmin() => User.IsInRole("ROLE_ADMIN") || User.IsInRole("ADMIN");

        /// <summary>
        /// Booking documents carry both usernames, the agreed price, proof URLs and the payment
        /// saga projection, so reads are restricted to the two parties plus administrators.
        /// Shared by <see cref="Get"/> and <see cref="GetPaymentStatus"/> so the two read paths
        /// cannot drift apart.
        /// </summary>
        private bool CallerMayViewBooking(Booking booking, string caller)
        {
            var isParty = string.Equals(
                    booking.RequesterUsername,
                    caller,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    booking.TaskMasterUsername,
                    caller,
                    StringComparison.OrdinalIgnoreCase);

            return isParty || IsAdmin();
        }

        private string? BearerFromRequest()
        {
            var h = Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(h) && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return h.Substring("Bearer ".Length).Trim();
            }
            return null;
        }
    }
}
