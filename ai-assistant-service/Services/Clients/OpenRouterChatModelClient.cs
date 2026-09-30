using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Contracts;
using ai_assistant_service.Services.Telemetry;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Services.Clients;

/// <summary>
/// Translates shared chat contracts to OpenRouter's OpenAI-compatible
/// non-streaming Chat Completions API.
/// </summary>
public sealed class OpenRouterChatModelClient : IChatModelClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly OpenRouterOptions _options;
    private readonly ILogger<OpenRouterChatModelClient> _logger;

    public OpenRouterChatModelClient(
        HttpClient httpClient,
        IOptions<OpenRouterOptions> options,
        ILogger<OpenRouterChatModelClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ChatModelResponse> ChatAsync(
        ChatModelRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = Stopwatch.GetTimestamp();
        var retryCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var failure = Failure(ChatModelFailureCategory.Timeout, request.Model);
                if (!await TryDelayForRetryAsync(
                        failure,
                        request,
                        startedAt,
                        retryCount,
                        cancellationToken))
                {
                    throw failure;
                }
            }
            catch (HttpRequestException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = Failure(
                    ChatModelFailureCategory.ProviderUnavailable,
                    request.Model);
                if (!await TryDelayForRetryAsync(
                        failure,
                        request,
                        startedAt,
                        retryCount,
                        cancellationToken))
                {
                    throw failure;
                }
            }
            catch (IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = Failure(
                    ChatModelFailureCategory.ProviderUnavailable,
                    request.Model);
                if (!await TryDelayForRetryAsync(
                        failure,
                        request,
                        startedAt,
                        retryCount,
                        cancellationToken))
                {
                    throw failure;
                }
            }
            catch (JsonException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Failure(ChatModelFailureCategory.ProtocolError, request.Model);
            }
            catch (ChatModelException failure)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await TryDelayForRetryAsync(
                        failure,
                        request,
                        startedAt,
                        retryCount,
                        cancellationToken))
                {
                    throw;
                }
            }

            retryCount++;
        }
    }

    private async Task<ChatModelResponse> SendAsync(
        ChatModelRequest request,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            $"{_options.BaseUrl!.Trim().TrimEnd('/')}/chat/completions",
            UriKind.Absolute);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(CreatePayload(request), options: JsonOptions)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _options.ApiKey!.Trim());

        if (!string.IsNullOrWhiteSpace(_options.HttpReferer))
        {
            message.Headers.TryAddWithoutValidation("HTTP-Referer", _options.HttpReferer.Trim());
            message.Headers.TryAddWithoutValidation("X-OpenRouter-Title", _options.AppTitle!.Trim());
            message.Headers.TryAddWithoutValidation("X-OpenRouter-App-Visibility", "hidden");
        }

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var status = (int)response.StatusCode;
        var retryAfter = ParseRetryAfter(response);
        using var document = await ReadResponseAsync(
            response,
            requireJson: response.IsSuccessStatusCode,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (document is not null
                && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out _))
            {
                throw ParseError(
                    document.RootElement,
                    request.Model,
                    status,
                    retryAfter);
            }

            throw Failure(
                Classify(status, limitSource: null, retryAfter),
                request.Model,
                status,
                providerErrorCode: null,
                retryAfter: retryAfter);
        }

        return ParseResponse(
            document?.RootElement ?? default,
            request,
            status,
            retryAfter);
    }

    private async Task<bool> TryDelayForRetryAsync(
        ChatModelException failure,
        ChatModelRequest request,
        long startedAt,
        int retryCount,
        CancellationToken cancellationToken)
    {
        if (retryCount >= _options.MaxRetries
            || request.RoundTimeout is null
            || !IsRetryable(failure.Category))
        {
            return false;
        }

        var delay = failure.Metadata.RetryAfter
            ?? BackoffWithJitter(retryCount);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var remaining = request.RoundTimeout.Value - elapsed;
        if (remaining <= TimeSpan.Zero || delay >= remaining)
        {
            return false;
        }

        AiAssistantTelemetry.RecordRetry(
            ChatModelProviders.OpenRouter,
            request.Model,
            failure.Category,
            failure.Metadata.HttpStatusCode,
            retryCount + 1,
            delay);

        if (delay > TimeSpan.Zero)
        {
            _logger.LogWarning(
                "Retrying OpenRouter model={Model}, category={Category}, status={Status}, provider request ID={ProviderRequestId}, retry={RetryCount}, delay milliseconds={DelayMilliseconds}",
                request.Model,
                failure.Category,
                failure.Metadata.HttpStatusCode,
                failure.Metadata.ProviderRequestId,
                retryCount + 1,
                delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken);
        }
        else
        {
            _logger.LogWarning(
                "Retrying OpenRouter model={Model}, category={Category}, status={Status}, provider request ID={ProviderRequestId}, retry={RetryCount}, delay milliseconds=0",
                request.Model,
                failure.Category,
                failure.Metadata.HttpStatusCode,
                failure.Metadata.ProviderRequestId,
                retryCount + 1);
        }

        return true;
    }

    private static bool IsRetryable(ChatModelFailureCategory category) =>
        category is ChatModelFailureCategory.TemporaryInFlightBudget
            or ChatModelFailureCategory.Timeout
            or ChatModelFailureCategory.RateLimited
            or ChatModelFailureCategory.ProviderUnavailable;

    private static TimeSpan BackoffWithJitter(int retryCount)
    {
        var exponentialMilliseconds = 200 * Math.Pow(2, retryCount);
        var jitterMilliseconds = Random.Shared.Next(25, 101);
        return TimeSpan.FromMilliseconds(
            Math.Min(exponentialMilliseconds + jitterMilliseconds, 2_000));
    }

    private static async Task<JsonDocument?> ReadResponseAsync(
        HttpResponseMessage response,
        bool requireJson,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
        }
        catch (JsonException) when (!requireJson)
        {
            return null;
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        var candidates = values.ToArray();
        if (candidates.Length != 1)
        {
            return null;
        }
        var raw = candidates[0];

        if (int.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds)
            && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var retryAt))
        {
            var delay = retryAt - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private RequestPayload CreatePayload(ChatModelRequest request) =>
        new(
            request.Model,
            request.Messages.Select(CreateMessage).ToArray(),
            request.Tools?.Select(tool => new ToolPayload(
                "function",
                new ToolDefinitionPayload(
                    tool.Name,
                    tool.Description,
                    tool.Parameters))).ToArray(),
            Stream: false,
            request.MaxOutputTokens,
            new ProviderPayload(
                _options.AllowFallbacks,
                _options.RequireParameters,
                _options.DataCollection.Trim().ToLowerInvariant()));

    private static MessagePayload CreateMessage(ChatModelMessage message) =>
        new(
            message.Role,
            message.Content,
            message.ToolCalls?.Select(call => new CallPayload(
                call.Id,
                "function",
                new FunctionPayload(
                    call.Name,
                    JsonSerializer.Serialize(call.Arguments, JsonOptions)))).ToArray(),
            message.ToolCallId);

    private static ChatModelException ParseError(
        JsonElement root,
        string requestedModel,
        int httpStatusCode,
        TimeSpan? retryAfter)
    {
        RequireObject(root);
        if (!root.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("code", out var codeProperty)
            || codeProperty.ValueKind != JsonValueKind.Number
            || !codeProperty.TryGetInt32(out var providerErrorCode))
        {
            return Failure(
                ChatModelFailureCategory.ProtocolError,
                requestedModel,
                httpStatusCode);
        }

        string? limitSource = null;
        if (error.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind != JsonValueKind.Null)
        {
            if (metadata.ValueKind != JsonValueKind.Object)
            {
                return Failure(
                    ChatModelFailureCategory.ProtocolError,
                    requestedModel,
                    httpStatusCode);
            }

            if (metadata.TryGetProperty("limit_source", out var limitSourceProperty)
                && limitSourceProperty.ValueKind != JsonValueKind.Null)
            {
                if (limitSourceProperty.ValueKind != JsonValueKind.String)
                {
                    return Failure(
                        ChatModelFailureCategory.ProtocolError,
                        requestedModel,
                        httpStatusCode);
                }

                limitSource = limitSourceProperty.GetString();
            }
        }

        var category = Classify(providerErrorCode, limitSource, retryAfter);
        return Failure(
            category,
            requestedModel,
            httpStatusCode,
            providerErrorCode,
            OptionalString(root, "id"),
            retryAfter,
            limitSource);
    }

    private static ChatModelFailureCategory Classify(
        int code,
        string? limitSource,
        TimeSpan? retryAfter) =>
        code switch
        {
            400 or 404 => ChatModelFailureCategory.InvalidRequest,
            401 => ChatModelFailureCategory.Authentication,
            402 when string.Equals(
                         limitSource,
                         "openrouter_in_flight_budget",
                         StringComparison.Ordinal)
                     && retryAfter.HasValue =>
                ChatModelFailureCategory.TemporaryInFlightBudget,
            402 => ChatModelFailureCategory.CreditsExhausted,
            403 => ChatModelFailureCategory.PolicyRejected,
            408 or 504 => ChatModelFailureCategory.Timeout,
            429 => ChatModelFailureCategory.RateLimited,
            500 or 502 or 503 => ChatModelFailureCategory.ProviderUnavailable,
            _ => ChatModelFailureCategory.UnknownProviderError
        };

    private static ChatModelResponse ParseResponse(
        JsonElement root,
        ChatModelRequest request,
        int httpStatusCode,
        TimeSpan? retryAfter)
    {
        RequireObject(root);
        if (root.TryGetProperty("error", out _))
        {
            throw ParseError(root, request.Model, httpStatusCode, retryAfter);
        }

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new JsonException("Missing response choices.");
        }

        var choice = choices[0];
        RequireObject(choice);
        var finishReason = OptionalString(choice, "finish_reason");
        if (finishReason is "length" or "max_tokens")
        {
            throw new JsonException("Truncated model response.");
        }

        if (finishReason is "content_filter" or "content_filtered")
        {
            throw Failure(
                ChatModelFailureCategory.PolicyRejected,
                request.Model,
                httpStatusCode,
                retryAfter: retryAfter);
        }

        if (finishReason is not null
            && finishReason is not ("stop" or "tool_calls" or "function_call"))
        {
            throw new JsonException("Unrecognized completion finish reason.");
        }

        if (!choice.TryGetProperty("message", out var message))
        {
            throw new JsonException("Missing assistant message.");
        }

        RequireObject(message);
        if (OptionalString(message, "role") != ChatModelRoles.Assistant)
        {
            throw new JsonException("Invalid assistant role.");
        }

        var content = OptionalString(message, "content");
        var allowedNames = (request.Tools ?? [])
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);
        var calls = new List<ChatModelToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind != JsonValueKind.Null)
        {
            if (toolCalls.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Invalid tool calls.");
            }

            foreach (var call in toolCalls.EnumerateArray())
            {
                RequireObject(call);
                var id = RequiredString(call, "id");
                if (id != id.Trim())
                {
                    throw new JsonException("Invalid tool-call ID.");
                }

                if (OptionalString(call, "type") is { } type && type != "function")
                {
                    throw new JsonException("Invalid tool-call type.");
                }

                if (!call.TryGetProperty("function", out var function))
                {
                    throw new JsonException("Missing tool function.");
                }

                RequireObject(function);
                var name = RequiredString(function, "name");
                if (name != name.Trim())
                {
                    throw new JsonException("Invalid tool name.");
                }

                if (!allowedNames.Contains(name))
                {
                    throw new JsonException("Unrecognized tool name.");
                }

                var argumentsJson = RequiredString(function, "arguments");
                using var argumentsDocument = JsonDocument.Parse(argumentsJson);
                if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Tool arguments must be an object.");
                }

                var argumentNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var argument in argumentsDocument.RootElement.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(argument.Name)
                        || !argumentNames.Add(argument.Name))
                    {
                        throw new JsonException("Invalid tool argument names.");
                    }
                }

                calls.Add(new ChatModelToolCall(id, name, argumentsDocument.RootElement));
            }
        }

        if (calls.Count == 0 && string.IsNullOrWhiteSpace(content))
        {
            throw new JsonException("Empty assistant output.");
        }

        return new ChatModelResponse(
            new ChatModelMessage(ChatModelRoles.Assistant, content, calls),
            providerRequestId: OptionalString(root, "id"),
            resolvedModel: OptionalString(root, "model"),
            usage: ParseUsage(root));
    }

    private static ChatModelUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireObject(usage);
        return new ChatModelUsage(
            OptionalTokenCount(usage, "prompt_tokens"),
            OptionalTokenCount(usage, "completion_tokens"),
            OptionalTokenCount(usage, "total_tokens"));
    }

    private static long? OptionalTokenCount(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var count)
            || count < 0)
        {
            throw new JsonException("Invalid token usage.");
        }

        return count;
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        var result = OptionalString(value, propertyName);
        return string.IsNullOrWhiteSpace(result)
            ? throw new JsonException($"Missing {propertyName}.")
            : result;
    }

    private static string? OptionalString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Invalid {propertyName}.");
        }

        return property.GetString();
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a JSON object.");
        }
    }

    private static ChatModelException Failure(
        ChatModelFailureCategory category,
        string requestedModel,
        int? status = null,
        int? providerErrorCode = null,
        string? providerRequestId = null,
        TimeSpan? retryAfter = null,
        string? limitSource = null) =>
        new(
            category,
            new ChatModelFailureMetadata(
                provider: ChatModelProviders.OpenRouter,
                requestedModel: requestedModel,
                httpStatusCode: status,
                providerErrorCode: providerErrorCode,
                providerRequestId: providerRequestId,
                retryAfter: retryAfter,
                limitSource: limitSource));

    private sealed record RequestPayload(
        string Model,
        IReadOnlyList<MessagePayload> Messages,
        IReadOnlyList<ToolPayload>? Tools,
        bool Stream,
        int MaxTokens,
        ProviderPayload Provider);

    private sealed record MessagePayload(
        string Role,
        string? Content,
        IReadOnlyList<CallPayload>? ToolCalls,
        string? ToolCallId);

    private sealed record CallPayload(string Id, string Type, FunctionPayload Function);

    private sealed record FunctionPayload(string Name, string Arguments);

    private sealed record ToolPayload(string Type, ToolDefinitionPayload Function);

    private sealed record ToolDefinitionPayload(
        string Name,
        string Description,
        JsonElement Parameters);

    private sealed record ProviderPayload(
        bool AllowFallbacks,
        bool RequireParameters,
        string DataCollection);
}
