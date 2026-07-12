using Microsoft.AspNetCore.Mvc;
using payment_service.Contracts;
using payment_service.Services;

namespace payment_service.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PaymentController : ControllerBase
    {
        private readonly IPaymentService _paymentService;

        public PaymentController(IPaymentService paymentService)
        {
            _paymentService = paymentService;
        }

        [HttpPost("process")]
        public async Task<IActionResult> ProcessPayment([FromBody] PaymentRequest request)
        {
            if (request.Amount <= 0)
            {
                return BadRequest(new { error = "Amount must be greater than zero." });
            }

            var transaction = await _paymentService.ProcessPaymentAsync(request);
            return Ok(transaction);
        }

        /// <summary>
        /// Lookup used by the caller's saga reconciliation job to check "did this charge
        /// actually happen?" for a sagaId whose SagaState is stuck in STARTED (e.g. after a
        /// crash between charging and recording the outcome). See PAYMENT_SAGA_SPEC.md.
        /// </summary>
        [HttpGet("transaction/{sagaId:guid}")]
        public async Task<IActionResult> GetTransactionBySagaId(Guid sagaId)
        {
            var transaction = await _paymentService.GetTransactionBySagaIdAsync(sagaId);
            if (transaction == null)
            {
                return NotFound();
            }

            return Ok(transaction);
        }
    }
}
