using System.Security.Claims;
using calendar_service.MessageQueue;
using calendar_service.Model;
using calendar_service.Services.Clients;
using calendar_service.Services.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace calendar_service.Controllers
{
    [ApiController]
    [Route("api/booking")]
    public class BookingController : ControllerBase
    {
        private readonly IBookingService _service;
        private readonly ITaskMasterApiClient _taskMasterClient;
        private readonly IPaymentApiClient _paymentClient;
        private readonly INotificationProducer _notifications;
        private readonly ILogger<BookingController> _logger;

        public BookingController(
            IBookingService service,
            ITaskMasterApiClient taskMasterClient,
            IPaymentApiClient paymentClient,
            INotificationProducer notifications,
            ILogger<BookingController> logger)
        {
            _service = service;
            _taskMasterClient = taskMasterClient;
            _paymentClient = paymentClient;
            _notifications = notifications;
            _logger = logger;
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
            public decimal InvoiceAmount { get; set; }
        }

        public class PayDto
        {
            public string CardNumber { get; set; } = string.Empty;
            public string ExpiryDate { get; set; } = string.Empty;
            public string CVV { get; set; } = string.Empty;
            public string OwnerName { get; set; } = string.Empty;
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
        /// GET <c>/api/booking/{id}</c> — returns a single booking by id. No ownership check;
        /// rely on the booking id being non-guessable (Mongo ObjectId).
        /// </summary>
        /// <response code="200">The booking.</response>
        /// <response code="404">No booking with that id.</response>
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(string id)
        {
            var item = await _service.GetByIdAsync(id);
            if (item == null) return NotFound();
            return Ok(item);
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
                    message = $"Your booking from {result.Accepted.SlotStart:yyyy-MM-dd HH:mm} to {result.Accepted.SlotEnd:HH:mm} UTC was accepted.",
                    actionType = "VIEW_OUTGOING_BOOKING_REQUEST",
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
        /// completed job (a file/image URL already uploaded elsewhere) plus the invoice amount.
        /// Moves the booking from ACCEPTED to IMPLEMENTED and notifies the requester that
        /// payment is due.
        /// </summary>
        /// <response code="200">The updated booking.</response>
        /// <response code="400">Missing proof file URL or invoice amount &lt;= 0.</response>
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
                var updated = await _service.SubmitProofAsync(id, caller, body.ProofFileUrl, body.InvoiceAmount);

                await _notifications.PublishAsync(new
                {
                    type = "BOOKING_PAYMENT_REQUIRED",
                    recipientUsername = updated.RequesterUsername,
                    message = $"{updated.TaskMasterUsername} submitted proof of the completed job. " +
                              $"Please pay ${updated.InvoiceAmount:0.00} to complete this booking.",
                    actionType = "VIEW_PAYMENT_REQUEST",
                    actionPayload = new Dictionary<string, string>
                    {
                        { "bookingId", updated.Id ?? string.Empty },
                        { "taskMasterId", updated.TaskMasterId }
                    }
                });
                return Ok(updated);
            }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        }

        /// <summary>
        /// POST <c>/api/booking/{id}/pay</c> — requester pays the invoice. The card details are
        /// forwarded server-to-server to payment-service's <c>/api/payment/process</c> endpoint
        /// (the frontend never calls payment-service directly), and the resulting transaction is
        /// verified (APPROVED status, amount matches the invoice) before the booking is moved
        /// from IMPLEMENTED to COMPLETED. Notifies the TaskMaster that payment was received.
        /// </summary>
        /// <response code="200">The updated booking.</response>
        /// <response code="400">Missing card details, or booking has no invoice amount.</response>
        /// <response code="401">No authenticated caller.</response>
        /// <response code="402">Payment was declined or payment-service could not be reached.</response>
        /// <response code="403">Caller is not the requester.</response>
        /// <response code="404">No booking with that id.</response>
        /// <response code="409">Booking is not IMPLEMENTED.</response>
        [HttpPost("{id}/pay")]
        public async Task<IActionResult> Pay(string id, [FromBody] PayDto body)
        {
            var caller = CurrentUsername();
            if (string.IsNullOrEmpty(caller)) return Unauthorized();
            if (body == null || string.IsNullOrWhiteSpace(body.CardNumber) || string.IsNullOrWhiteSpace(body.CVV)
                || string.IsNullOrWhiteSpace(body.ExpiryDate) || string.IsNullOrWhiteSpace(body.OwnerName))
            {
                return BadRequest("cardNumber, expiryDate, cvv and ownerName are required");
            }

            var booking = await _service.GetByIdAsync(id);
            if (booking == null) return NotFound();
            if (!string.Equals(booking.RequesterUsername, caller, StringComparison.OrdinalIgnoreCase)) return Forbid();
            if (booking.Status != Booking.StatusImplemented)
            {
                return Conflict(new { error = $"Booking is {booking.Status} and cannot be paid" });
            }
            if (booking.InvoiceAmount == null || booking.InvoiceAmount <= 0)
            {
                return BadRequest("Booking has no invoice amount to pay");
            }

            var card = new CreditCardInfo
            {
                CardNumber = body.CardNumber,
                ExpiryDate = body.ExpiryDate,
                CVV = body.CVV,
                OwnerName = body.OwnerName
            };
            var transaction = await _paymentClient.ProcessPaymentAsync(card, booking.InvoiceAmount.Value, HttpContext.RequestAborted);
            if (transaction == null)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Could not reach payment service" });
            }
            if (!string.Equals(transaction.Status, PaymentTransactionResult.StatusApproved, StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(402, new { error = "Payment was declined" });
            }
            if (transaction.Amount != booking.InvoiceAmount.Value)
            {
                _logger.LogWarning("Payment amount {PaymentAmount} did not match invoice amount {InvoiceAmount} for booking {BookingId}",
                    transaction.Amount, booking.InvoiceAmount.Value, id);
                return StatusCode(402, new { error = "Payment amount does not match invoice amount" });
            }

            try
            {
                var updated = await _service.CompletePaymentAsync(id, caller, transaction.Id);

                await _notifications.PublishAsync(new
                {
                    type = "BOOKING_PAYMENT_RECEIVED",
                    recipientUsername = updated.TaskMasterUsername,
                    message = $"{updated.RequesterUsername} paid ${updated.InvoiceAmount:0.00} for the booking from " +
                              $"{updated.SlotStart:yyyy-MM-dd HH:mm} to {updated.SlotEnd:HH:mm} UTC.",
                    actionType = "VIEW_INCOMING_BOOKING_REQUEST",
                    actionPayload = new Dictionary<string, string>
                    {
                        { "bookingId", updated.Id ?? string.Empty },
                        { "taskMasterId", updated.TaskMasterId }
                    }
                });
                return Ok(updated);
            }
            catch (UnauthorizedAccessException) { return Forbid(); }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
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
