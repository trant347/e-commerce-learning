using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services.Clients;

public sealed class OllamaClient : IOllamaClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaClient> _logger;

    public OllamaClient(HttpClient httpClient, ILogger<OllamaClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var payload = new OllamaGenerateRequest
        {
            Model = model,
            Prompt = userPrompt,
            System = systemPrompt,
            Stream = false
        };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/generate", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken: cancellationToken);
            return body?.Response ?? "No response from Ollama.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Ollama generate API");
            return "I could not reach the AI model right now.";
        }
    }

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("system")]
        public string System { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;
    }
}
