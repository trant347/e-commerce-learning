using System.Diagnostics;
using System.Security.Claims;
using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services;
using calendar_service.Services.Clients;
using calendar_service.Services.Contracts;
using Microsoft.AspNetCore.Mvc;
using Payment.Contracts.V1;

namespace calendar_service.Controllers
{
    [ApiController]
    [Route("api/booking")]
    public class BookingController : ControllerBase
    {
        private readonly IBookingService _service;
        private readonly ITaskMasterApiClient _taskMasterClient;
        private readonly ISagaStateService _sagaStateService;
        private readonly INotificationProducer _notifications;
        private readonly IConfiguration _configuration;

        public BookingController(
            IBookingService service,
            ITaskMasterApiClient taskMasterClient,
            ISagaStateService sagaStateService,
            INotificationProducer notifications,
            IConfiguration configuration)
        {
            _service = service;
            _taskMasterClient = taskMasterClient;
            _sagaStateService = sagaStateService;
            _notifications = notifications;
            _configuration = configuration;
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
            var isAdmin = User.IsInRole("ROLE_ADMIN") || User.IsInRole("ADMIN");

            var tm = await _taskMasterClient.GetByIdAsync(taskMasterId, BearerFromRequest(), HttpContext.RequestAborted);
            var callerIsOwner = !string.IsNullOrEmpty(caller)
                && !string.IsNullOrEmpty(tm?.OwnerUsername)
                && string.Equals(caller, tm!.OwnerUsername, StringComparison.OrdinalIgnoreCase);

            var slots = await _service.GetTimetableAsync(taskMasterId, caller, isAdmin, callerIsOwner);
            return Ok(slots);
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

            try
            {
                var created = await _service.CreateAsync(
                    dto.TaskMasterId, tm.OwnerUsername!, caller, dto.SlotStart, dto.DurationHours, dto.Message,
                    dto.OfferedRatePerHour);

                await _notifications.PublishAsync(new
                {
                    type = "BOOKING_REQUEST_SUBMITTED",
                    recipientUsername = tm.OwnerUsername,
                    message = $"{caller} requested to book you from {created.SlotStart:yyyy-MM-dd HH:mm} to {created.SlotEnd:HH:mm} UTC.",
                    actionType = "VIEW_INCOMING_BOOKING_REQUEST",
                    actionPayload = new Dictionary<string, string>
                    {
                        { "bookingId", created.Id ?? string.Empty },
                        { "taskMasterId", dto.TaskMasterId }
                    }
                });
                return Ok(created);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { error = ex.Message });
            }
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
            return Ok(list);
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
            return Ok(list);
        }

        /// <summary>
        /// GET <c>/api/booking/{id}</c> — returns a single booking by id.
        /// </summary>
        /// <response code="200">The booking.</response>
        /// <response code="404">No booking with that id.</response>
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(string id)
        {
            var item = await _service.GetByIdAsync(id);
            if (item == null) return NotFound();
            await PopulateLatestPaymentAsync(item);
            return Ok(item);
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

            var ownsBooking = string.Equals(
                    booking.RequesterUsername,
                    caller,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    booking.TaskMasterUsername,
                    caller,
                    StringComparison.OrdinalIgnoreCase);
            if (!ownsBooking
                && !User.IsInRole("ROLE_ADMIN")
                && !User.IsInRole("ADMIN"))
            {
                return Forbid();
            }

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
            try
            {
                var result = await _service.AcceptAsync(id, caller, body?.Message);

                await _notifications.PublishAsync(new
                {
                    type = "BOOKING_REQUEST_ACCEPTED",
                    recipientUsername = result.Accepted.RequesterUsername,
                    message = $"Your booking from {result.Accepted.SlotStart:yyyy-MM-dd HH:mm} to {result.Accepted.SlotEnd:HH:mm} UTC was accepted. Fund escrow to confirm the work.",
                    actionType = "VIEW_PAYMENT_REQUEST",
                    actionPayload = new Dictionary<string, string>
                    {
                        { "bookingId", result.Accepted.Id ?? string.Empty },
                        { "taskMasterId", result.Accepted.TaskMasterId }
                    }
                });

                foreach (var declined in result.AutoDeclined)
                {
                    await _notifications.PublishAsync(new
                    {
                        type = "BOOKING_REQUEST_DECLINED",
                        recipientUsername = declined.RequesterUsername,
                        message = $"Your booking from {declined.SlotStart:yyyy-MM-dd HH:mm} to {declined.SlotEnd:HH:mm} UTC was auto-declined (slot taken).",
                        actionType = "VIEW_OUTGOING_BOOKING_REQUEST",
                        actionPayload = new Dictionary<string, string>
                        {
                            { "bookingId", declined.Id ?? string.Empty },
                            { "taskMasterId", declined.TaskMasterId }
                        }
                    });
                }
                return Ok(result.Accepted);
            }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
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
            try
            {
                var updated = await _service.DeclineAsync(id, caller, body?.Message);
                if (updated == null) return NotFound();

                await _notifications.PublishAsync(new
                {
                    type = "BOOKING_REQUEST_DECLINED",
                    recipientUsername = updated.RequesterUsername,
                    message = $"Your booking from {updated.SlotStart:yyyy-MM-dd HH:mm} to {updated.SlotEnd:HH:mm} UTC was declined.",
                    actionType = "VIEW_OUTGOING_BOOKING_REQUEST",
                    actionPayload = new Dictionary<string, string>
                    {
                        { "bookingId", updated.Id ?? string.Empty },
                        { "taskMasterId", updated.TaskMasterId }
                    }
                });
                return Ok(updated);
            }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
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
        /// <response code="409">Booking is not ACCEPTED.</response>
        [HttpPost("{id}/submit-proof")]
        public async Task<IActionResult> SubmitProof(string id, [FromBody] SubmitProofDto body)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();
            if (body == null || string.IsNullOrWhiteSpace(body.ProofFileUrl))
            {
                return BadRequest("proofFileUrl is required");
            }

            try
            {
                var booking = await _service.GetByIdAsync(id);
                if (booking == null) return NotFound();
                if (!booking.EscrowId.HasValue)
                {
                    return Conflict(new
                    {
                        error = "Proof submission requires an escrow-funded booking"
                    });
                }
                if (await HasActiveOperationAsync(
                        id,
                        PaymentOperation.ReleaseEscrow))
                {
                    return Conflict(new
                    {
                        error = "Escrow release for this booking is already being processed"
                    });
                }

                var updated = await _service.RequestEscrowReleaseAsync(
                    id,
                    caller,
                    body.ProofFileUrl);
                var accepted = await EnqueueEscrowTransferAsync(
                    updated,
                    PaymentOperation.ReleaseEscrow);
                return Accepted(accepted.StatusUrl, accepted);
            }
            catch (ActivePaymentSagaException ex)
            {
                return Conflict(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        }

        /// <summary>
        /// POST <c>/api/booking/{id}/start-work</c> — starts work only after escrow funding.
        /// </summary>
        [HttpPost("{id}/start-work")]
        public async Task<IActionResult> StartWork(string id)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();

            try
            {
                var updated = await _service.StartWorkAsync(id, caller);
                return Ok(updated);
            }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
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

            try
            {
                var existing = await _service.GetByIdAsync(id);
                if (existing == null) return NotFound();
                if (await HasActiveOperationAsync(
                        id,
                        PaymentOperation.RefundEscrow))
                {
                    return Conflict(new
                    {
                        error = "Escrow refund for this booking is already being processed"
                    });
                }

                var updated = await _service.RequestCancellationAsync(id, caller);
                var refundRequested = updated.RefundRequestedAt.HasValue
                    && updated.Status != Booking.StatusCancelled;
                PaymentAcceptedResponseV1? accepted = null;
                if (refundRequested)
                {
                    accepted = await EnqueueEscrowTransferAsync(
                        updated,
                        PaymentOperation.RefundEscrow);
                }

                if (!refundRequested)
                {
                    await _notifications.PublishAsync(new
                    {
                        type = "BOOKING_CANCELLED",
                        recipientUsername = updated.TaskMasterUsername,
                        message = $"{updated.RequesterUsername} cancelled the booking.",
                        actionType = "VIEW_INCOMING_BOOKING_REQUEST",
                        actionPayload = new Dictionary<string, string>
                        {
                            { "bookingId", updated.Id ?? string.Empty },
                            { "taskMasterId", updated.TaskMasterId }
                        }
                    });
                }
                return accepted == null
                    ? Ok(updated)
                    : Accepted(accepted.StatusUrl, accepted);
            }
            catch (ActivePaymentSagaException ex)
            {
                return Conflict(new { error = ex.Message });
            }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
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

            return await EnqueueEscrowFundingAsync(id, body, caller);
        }

        private async Task<IActionResult> EnqueueEscrowFundingAsync(
            string bookingId,
            PayDto body,
            string caller)
        {
            if (string.IsNullOrWhiteSpace(body.PaymentMethodToken))
            {
                return BadRequest("paymentMethodToken is required");
            }

            var booking = await _service.GetByIdAsync(bookingId);
            if (booking == null) return NotFound();
            if (!string.Equals(booking.RequesterUsername, caller, StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }
            if (booking.Status != Booking.StatusAccepted)
            {
                return Conflict(new
                {
                    error = $"Booking is {booking.Status} and is not eligible for escrow funding"
                });
            }
            if (booking.AgreedAmount is null or <= 0
                || string.IsNullOrWhiteSpace(booking.AgreedCurrency))
            {
                return Conflict(new
                {
                    error = "Booking price and currency must be fixed before escrow funding"
                });
            }
            if (booking.WorkStartedAt.HasValue)
            {
                return Conflict(new { error = "Work has already started for this booking" });
            }
            if (booking.EscrowId.HasValue
                && booking.EscrowStatus != EscrowStatus.Pending)
            {
                return Conflict(new
                {
                    error = $"Booking escrow is {booking.EscrowStatus} and cannot be funded again"
                });
            }

            var latestSaga = await _sagaStateService.GetLatestByBookingIdAsync(bookingId);
            if (latestSaga?.Status == SagaState.StatusStarted)
            {
                return Conflict(new
                {
                    error = "Escrow funding for this booking is already being processed"
                });
            }

            var escrowId = booking.EscrowId ?? Guid.NewGuid();
            if (!booking.EscrowId.HasValue)
            {
                try
                {
                    booking = await _service.AttachEscrowAsync(bookingId, caller, escrowId);
                }
                catch (UnauthorizedAccessException)
                {
                    return Forbid();
                }
                catch (KeyNotFoundException)
                {
                    return NotFound();
                }
                catch (InvalidOperationException ex)
                {
                    return Conflict(new { error = ex.Message });
                }
            }

            var custodyUserId = _configuration["Escrow:CustodyUserId"];
            if (string.IsNullOrWhiteSpace(custodyUserId))
            {
                throw new InvalidOperationException("Escrow:CustodyUserId is required");
            }

            var sagaId = Guid.NewGuid();
            var request = new PaymentRequestedV1
            {
                SagaId = sagaId,
                EscrowId = escrowId,
                BookingId = bookingId,
                Operation = PaymentOperation.FundEscrow,
                Amount = booking.AgreedAmount!.Value,
                Currency = booking.AgreedCurrency!,
                PayerUserId = caller,
                PayeeUserId = custodyUserId,
                TaskMasterUserId = booking.TaskMasterUsername,
                PaymentMethodToken = body.PaymentMethodToken.Trim()
            };

            try
            {
                await _sagaStateService.EnqueueAsync(
                    request,
                    Activity.Current?.Id,
                    HttpContext.RequestAborted);
            }
            catch (ActivePaymentSagaException ex)
            {
                return Conflict(new { error = ex.Message });
            }

            var response = new PaymentAcceptedResponseV1
            {
                SagaId = sagaId,
                EscrowId = escrowId,
                StatusUrl = $"/api/booking/payment-status/{sagaId:D}"
            };
            return Accepted(response.StatusUrl, response);
        }

        private async Task<PaymentAcceptedResponseV1> EnqueueEscrowTransferAsync(
            Booking booking,
            string operation)
        {
            if (!booking.EscrowId.HasValue
                || booking.AgreedAmount is null or <= 0
                || string.IsNullOrWhiteSpace(booking.AgreedCurrency))
            {
                throw new InvalidOperationException(
                    "Booking escrow, fixed amount, and currency are required");
            }

            var custodyUserId = _configuration["Escrow:CustodyUserId"];
            if (string.IsNullOrWhiteSpace(custodyUserId))
            {
                throw new EscrowConfigurationException(
                    "Escrow:CustodyUserId is required");
            }

            var payeeUserId = operation switch
            {
                PaymentOperation.ReleaseEscrow => booking.TaskMasterUsername,
                PaymentOperation.RefundEscrow => booking.RequesterUsername,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(operation),
                    operation,
                    "Only release and refund transfers can be enqueued here.")
            };
            var sagaId = Guid.NewGuid();
            var request = new PaymentRequestedV1
            {
                SagaId = sagaId,
                EscrowId = booking.EscrowId.Value,
                BookingId = booking.Id
                    ?? throw new InvalidOperationException(
                        "Booking id is required"),
                Operation = operation,
                Amount = booking.AgreedAmount.Value,
                Currency = booking.AgreedCurrency,
                PayerUserId = custodyUserId,
                PayeeUserId = payeeUserId,
                TaskMasterUserId = booking.TaskMasterUsername
            };

            await _sagaStateService.EnqueueAsync(
                request,
                Activity.Current?.Id,
                HttpContext.RequestAborted);

            return new PaymentAcceptedResponseV1
            {
                SagaId = sagaId,
                EscrowId = booking.EscrowId.Value,
                StatusUrl = $"/api/booking/payment-status/{sagaId:D}"
            };
        }

        private async Task<bool> HasActiveOperationAsync(
            string bookingId,
            string operation)
        {
            var latest = await _sagaStateService.GetLatestByBookingIdAsync(
                bookingId);
            return latest?.Status == SagaState.StatusStarted
                && latest.Operation == operation;
        }

        /// <summary>
        /// Exposes the latest durable saga so the frontend can resume status polling after a page
        /// reload instead of relying on transient client state.
        /// </summary>
        private async Task PopulateLatestPaymentAsync(Booking booking)
        {
            if (booking.Id == null) return;
            var latestSaga = await _sagaStateService.GetLatestByBookingIdAsync(booking.Id);
            if (latestSaga == null) return;

            booking.PaymentPending = latestSaga.Status == SagaState.StatusStarted;
            if (!latestSaga.EscrowId.HasValue || string.IsNullOrWhiteSpace(latestSaga.Operation))
            {
                return;
            }

            booking.LatestPaymentSagaId = latestSaga.SagaId;
            booking.LatestPaymentStatus = latestSaga.Status == SagaState.StatusStarted
                ? PaymentStatusResponseV1.PendingStatus
                : latestSaga.Status;
            booking.LatestPaymentOperation = latestSaga.Operation;
            booking.LatestPaymentFailureReason = latestSaga.FailureReason;
        }

        private string? CurrentUsername() =>
            User?.FindFirst(ClaimTypes.Name)?.Value ?? User?.Identity?.Name;

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
