using System.Text.Json;

namespace calendar_service.Services.Clients
{
    public class PaymentTransactionResult
    {
        public const string StatusApproved = "APPROVED";

        public string Id { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string MaskedCardNumber { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        /// <summary>Human-readable reason for a decline (e.g. "Insufficient balance"), if any.</summary>
        public string? DeclineReason { get; set; }

        /// <summary>Idempotency key echoed back by payment-service, if one was sent.</summary>
        public Guid? SagaId { get; set; }
    }

    public interface IPaymentApiClient
    {
        /// <summary>
        /// Retained temporarily so reconciliation can resolve legacy synchronous sagas that
        /// were already STARTED before new legacy payments were disabled.
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

        public PaymentApiClient(HttpClient http)
        {
            _http = http;
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
