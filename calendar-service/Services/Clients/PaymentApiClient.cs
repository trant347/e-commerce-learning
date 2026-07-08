using System.Net.Http.Json;
using System.Text.Json;

namespace calendar_service.Services.Clients
{
    public class CreditCardInfo
    {
        public string CardNumber { get; set; } = string.Empty;
        public string ExpiryDate { get; set; } = string.Empty;
        public string CVV { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
    }

    public class PaymentTransactionResult
    {
        public const string StatusApproved = "APPROVED";

        public string Id { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string MaskedCardNumber { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public interface IPaymentApiClient
    {
        /// <summary>
        /// Calls payment-service's <c>POST /api/payment/process</c> server-to-server so the
        /// resulting transaction (amount, status) can be trusted and verified before a booking
        /// is marked as paid — the frontend never talks to payment-service directly, and never
        /// asserts its own "payment succeeded" outcome to calendar-service.
        /// </summary>
        Task<PaymentTransactionResult?> ProcessPaymentAsync(CreditCardInfo card, decimal amount, CancellationToken ct);
    }

    public class PaymentApiClient : IPaymentApiClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<PaymentApiClient> _logger;

        public PaymentApiClient(HttpClient http, ILogger<PaymentApiClient> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<PaymentTransactionResult?> ProcessPaymentAsync(CreditCardInfo card, decimal amount, CancellationToken ct)
        {
            try
            {
                var payload = new
                {
                    creditCard = new
                    {
                        cardNumber = card.CardNumber,
                        expiryDate = card.ExpiryDate,
                        cvv = card.CVV,
                        ownerName = card.OwnerName
                    },
                    amount,
                    currency = "USD"
                };

                var resp = await _http.PostAsJsonAsync("/api/payment/process", payload, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("payment-service returned {Status} processing payment", resp.StatusCode);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<PaymentTransactionResult>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reach payment-service to process payment");
                return null;
            }
        }
    }
}
