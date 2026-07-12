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

        /// <summary>Idempotency key echoed back by payment-service, if one was sent.</summary>
        public Guid? SagaId { get; set; }
    }

    public interface IPaymentApiClient
    {
        /// <summary>
        /// Calls payment-service's <c>POST /api/payment/process</c> server-to-server so the
        /// resulting transaction (amount, status) can be trusted and verified before a booking
        /// is marked as paid — the frontend never talks to payment-service directly, and never
        /// asserts its own "payment succeeded" outcome to calendar-service.
        /// </summary>
        /// <param name="sagaId">
        /// Idempotency key from the caller's SagaState (see PAYMENT_SAGA_SPEC.md). When supplied,
        /// payment-service dedupes retried requests with the same id instead of double-charging.
        /// </param>
        Task<PaymentTransactionResult?> ProcessPaymentAsync(CreditCardInfo card, decimal amount, CancellationToken ct, Guid? sagaId = null);

        /// <summary>
        /// Calls payment-service's <c>GET /api/payment/transaction/{sagaId}</c> so the saga
        /// reconciliation job can check "did this charge actually happen?" for a sagaId whose
        /// SagaState is stuck in STARTED.
        /// </summary>
        /// <returns>The transaction, or null if payment-service has no record of this sagaId (404) —
        /// meaning the charge genuinely never happened and it's safe to mark the saga FAILED.</returns>
        /// <exception cref="PaymentServiceUnavailableException">
        /// payment-service could not be reached or returned an unexpected error. Distinct from a
        /// confirmed "not found" so the caller does NOT mark the saga FAILED here — a charge may
        /// still have happened and this transient failure should be retried instead.
        /// </exception>
        Task<PaymentTransactionResult?> GetTransactionBySagaIdAsync(Guid sagaId, CancellationToken ct);
    }

    /// <summary>
    /// Thrown when payment-service can't be reached or errors on a lookup call, as opposed to a
    /// confirmed "no such transaction" (404). Callers (e.g. saga reconciliation) must not treat
    /// this the same as "not found", since a charge may still have gone through.
    /// </summary>
    public class PaymentServiceUnavailableException : Exception
    {
        public PaymentServiceUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
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

        public async Task<PaymentTransactionResult?> ProcessPaymentAsync(CreditCardInfo card, decimal amount, CancellationToken ct, Guid? sagaId = null)
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
                    currency = "USD",
                    sagaId
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

        public async Task<PaymentTransactionResult?> GetTransactionBySagaIdAsync(Guid sagaId, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await _http.GetAsync($"/api/payment/transaction/{sagaId}", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new PaymentServiceUnavailableException($"Failed to reach payment-service to look up sagaId={sagaId}", ex);
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                throw new PaymentServiceUnavailableException(
                    $"payment-service returned {resp.StatusCode} looking up sagaId={sagaId}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<PaymentTransactionResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
    }
}
