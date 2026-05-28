using System.Text.Json;

namespace calendar_service.Services.Clients
{
    public interface ITaskMasterApiClient
    {
        Task<TaskMasterLookup?> GetByIdAsync(string id, string? bearerToken, CancellationToken ct);
    }

    public class TaskMasterLookup
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? OwnerUsername { get; set; }
    }

    public class TaskMasterApiClient : ITaskMasterApiClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<TaskMasterApiClient> _logger;

        public TaskMasterApiClient(HttpClient http, ILogger<TaskMasterApiClient> logger)
        {
            _http = http;
            _logger = logger;
        }

        public async Task<TaskMasterLookup?> GetByIdAsync(string id, string? bearerToken, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"/products/{Uri.EscapeDataString(id)}");
                if (!string.IsNullOrEmpty(bearerToken))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerToken);
                }
                var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TaskMaster lookup {Id} returned {Status}", id, resp.StatusCode);
                    return null;
                }
                var json = await resp.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<TaskMasterLookup>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch TaskMaster {Id} from product-service", id);
                return null;
            }
        }
    }
}
