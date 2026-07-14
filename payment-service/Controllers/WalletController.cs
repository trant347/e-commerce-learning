using Microsoft.AspNetCore.Mvc;
using payment_service.Services;

namespace payment_service.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WalletController : ControllerBase
    {
        private readonly IWalletService _walletService;

        public WalletController(IWalletService walletService)
        {
            _walletService = walletService;
        }

        /// <summary>
        /// Looks up a user's simulated wallet balance. Used by the frontend to show "you have
        /// $X available" before a booking payment is attempted, and by tests/diagnostics to
        /// verify a charge actually moved money between wallets.
        /// </summary>
        [HttpGet("{userId}")]
        public async Task<IActionResult> GetWallet(string userId)
        {
            var wallet = await _walletService.GetWalletAsync(userId);
            if (wallet == null)
            {
                return NotFound();
            }

            return Ok(wallet);
        }
    }
}
