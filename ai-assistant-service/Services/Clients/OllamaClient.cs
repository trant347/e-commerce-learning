using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services.Clients;

public sealed class OllamaClient : IOllamaClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaClient> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OllamaClient(HttpClient httpClient, ILogger<OllamaClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // ── Legacy single-turn generate ──────────────────────────────────────────
    public async Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Calling Ollama /api/generate with model={Model}, promptLength={PromptLength}", 
            model, userPrompt?.Length ?? 0);

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
            var result = body?.Response ?? "No response from Ollama.";

            _logger.LogInformation("Ollama /api/generate completed successfully, responseLength={ResponseLength}", 
                result.Length);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Ollama generate API");
            return "I could not reach the AI model right now.";
        }
    }

    // ── Multi-turn chat with tool-calling ────────────────────────────────────
    public async Task<OllamaChatMessage> ChatAsync(
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        IReadOnlyList<object>? tools,
        CancellationToken cancellationToken)
    {
        var payload = new OllamaChatRequest
        {
            Model = model,
            Messages = messages,
            Tools = tools,
            Stream = false,
            // qwen3 enables "thinking" by default which produces large <think> blocks
            // before any tool call and causes timeouts on CPU. Disable explicitly.
            Think = false
        };

        try
        {
            _logger.LogInformation("Calling Ollama /api/chat with {MessageCount} messages, {ToolCount} tools",
                messages.Count, tools?.Count ?? 0);

            using var response = await _httpClient.PostAsJsonAsync("/api/chat", payload, _jsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
                _jsonOptions, cancellationToken: cancellationToken);

            var message = body?.Message ?? new OllamaChatMessage { Role = "assistant", Content = "No response from Ollama." };

            _logger.LogInformation("Ollama /api/chat completed successfully: role={Role}, has_tool_calls={HasToolCalls}, contentLength={ContentLength}",
                message.Role, message.ToolCalls?.Count > 0, message.Content?.Length ?? 0);

            return message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Ollama /api/chat");
            return new OllamaChatMessage { Role = "assistant", Content = "I could not reach the AI model right now." };
        }
    }

    // ── Private request/response models ─────────────────────────────────────

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

    private sealed class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public IReadOnlyList<OllamaChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("tools")]
        public IReadOnlyList<object>? Tools { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("think")]
        public bool? Think { get; set; }
    }

    private sealed class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }
    }
}
