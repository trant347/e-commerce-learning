using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Contracts;
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

    public OpenRouterChatModelClient(
        HttpClient httpClient,
        IOptions<OpenRouterOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<ChatModelResponse> ChatAsync(
        ChatModelRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(
                    ChatModelFailureCategory.UnknownProviderError,
                    request.Model,
                    status);
            }

            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                JsonOptions,
                cancellationToken);
            var result = ParseResponse(document?.RootElement ?? default, request);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(ChatModelFailureCategory.Timeout, request.Model);
        }
        catch (HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(ChatModelFailureCategory.ProviderUnavailable, request.Model);
        }
        catch (IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(ChatModelFailureCategory.ProviderUnavailable, request.Model);
        }
        catch (JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(ChatModelFailureCategory.ProtocolError, request.Model);
        }
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

    private static ChatModelResponse ParseResponse(
        JsonElement root,
        ChatModelRequest request)
    {
        RequireObject(root);
        if (root.TryGetProperty("error", out _))
        {
            throw Failure(ChatModelFailureCategory.UnknownProviderError, request.Model, 200);
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
        if (finishReason == "length")
        {
            throw new JsonException("Truncated model response.");
        }

        if (finishReason == "content_filter")
        {
            throw Failure(ChatModelFailureCategory.PolicyRejected, request.Model, 200);
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
        int? status = null) =>
        new(
            category,
            new ChatModelFailureMetadata(
                provider: ChatModelProviders.OpenRouter,
                requestedModel: requestedModel,
                httpStatusCode: status));

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
