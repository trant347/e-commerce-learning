using System.Net;
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
            await EnsureSuccessAsync(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(
                _jsonOptions, cancellationToken: cancellationToken);

            var message = body?.Message ?? new OllamaChatMessage { Role = "assistant", Content = "No response from Ollama." };

            if (message.ToolCalls is not { Count: > 0 })
            {
                var allowedNames = (tools ?? []).Select(tool => JsonSerializer.SerializeToElement(tool))
                    .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!)
                    .ToHashSet(StringComparer.Ordinal);
                var inline = OllamaChatModelClient.RecoverInlineCall(
                    message.Content, allowedNames, new HashSet<string>(StringComparer.Ordinal));
                if (inline is not null)
                {
                    message.Content = null;
                    message.ToolCalls = [new OllamaToolCall
                    {
                        Function = new OllamaToolCallFunction
                        {
                            Name = inline.Name,
                            Arguments = inline.Arguments.ToDictionary(pair => pair.Key, pair => pair.Value)
                        }
                    }];
                }
            }

            _logger.LogInformation("Ollama /api/chat completed successfully: role={Role}, has_tool_calls={HasToolCalls}, contentLength={ContentLength}",
                message.Role, message.ToolCalls?.Count > 0, message.Content?.Length ?? 0);

            return message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Ollama /api/chat");
            return new OllamaChatMessage { Role = "assistant", Content = DescribeFailure(ex, model, cancellationToken) };
        }
    }

    public const string ModelUnavailableMessage =
        "The AI model is still being set up and isn't available yet. Please try again in a few minutes.";
    public const string TimeoutMessage =
        "The AI model took too long to respond. Please try again.";
    public const string UnreachableMessage =
        "I could not reach the AI model right now. Please try again shortly.";
    public const string ModelErrorMessage =
        "The AI model ran into a problem answering your request. Please try again shortly.";

    /// <summary>
    /// Maps a failed Ollama call to a user-facing message. Ollama answers 404 on
    /// /api/chat when the requested model has not been pulled,
    /// which happens while <c>ollama-init</c> is still downloading it after a fresh start.
    /// </summary>
    private string DescribeFailure(Exception ex, string model, CancellationToken cancellationToken)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
        {
            _logger.LogWarning(
                "Ollama model '{Model}' is not available yet (404). It may still be downloading; check the ollama-init container.",
                model);
            return ModelUnavailableMessage;
        }

        if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return TimeoutMessage;
        }

        if (ex is HttpRequestException { StatusCode: null })
        {
            return UnreachableMessage;
        }

        return ModelErrorMessage;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("Ollama returned {StatusCode}: {Body}",
            (int)response.StatusCode, body.Length > 300 ? body[..300] + "…" : body);
        throw new HttpRequestException(
            $"Ollama returned {(int)response.StatusCode} ({response.StatusCode}).",
            inner: null,
            statusCode: response.StatusCode);
    }

    // ── Private request/response models ─────────────────────────────────────

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
