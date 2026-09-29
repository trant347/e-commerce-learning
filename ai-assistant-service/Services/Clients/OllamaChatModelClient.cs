using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services.Clients;

/// <summary>
/// Translates shared chat contracts to Ollama's native API. Provider failures stay
/// typed; presentation and tool execution belong to the calling workflow.
/// </summary>
public sealed class OllamaChatModelClient : IChatModelClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;

    public OllamaChatModelClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<ChatModelResponse> ChatAsync(
        ChatModelRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = CreatePayload(request);
        int? status = null;

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "/api/chat", payload, JsonOptions, cancellationToken);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                throw Failure(Classify(response.StatusCode), request.Model, status);
            }

            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                JsonOptions, cancellationToken);
            var result = ParseResponse(document?.RootElement ?? default, request);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(ChatModelFailureCategory.Timeout, request.Model, status);
        }
        catch (HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(ChatModelFailureCategory.ProviderUnavailable, request.Model, status);
        }
        catch (IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(ChatModelFailureCategory.ProviderUnavailable, request.Model, status);
        }
        catch (JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure(ChatModelFailureCategory.ProtocolError, request.Model, status);
        }
    }

    private static ChatRequestPayload CreatePayload(ChatModelRequest request)
    {
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        var messages = new List<MessagePayload>();
        foreach (var message in request.Messages)
        {
            foreach (var call in message.ToolCalls ?? [])
            {
                if (!calls.TryAdd(call.Id, call.Name))
                {
                    throw Failure(ChatModelFailureCategory.InvalidRequest, request.Model);
                }
            }

            string? toolName = null;
            if (message.ToolCallId is not null && !calls.TryGetValue(message.ToolCallId, out toolName))
            {
                // Ollama needs the name, so the result must identify a preceding call.
                throw Failure(ChatModelFailureCategory.InvalidRequest, request.Model);
            }

            messages.Add(new MessagePayload(
                message.Role,
                message.Content ?? string.Empty,
                message.ToolCalls?.Select(call =>
                    new CallPayload(new FunctionPayload(call.Name, call.Arguments))).ToArray(),
                toolName));
        }

        return new ChatRequestPayload(
            request.Model,
            messages,
            request.Tools?.Select(tool => new ToolPayload(
                "function", new ToolDefinitionPayload(tool.Name, tool.Description, tool.Parameters))).ToArray(),
            new GenerationOptions(request.MaxOutputTokens));
    }

    private static ChatModelResponse ParseResponse(JsonElement root, ChatModelRequest request)
    {
        RequireObject(root);
        if (root.TryGetProperty("error", out _))
        {
            throw Failure(ChatModelFailureCategory.UnknownProviderError, request.Model, 200);
        }

        if (root.TryGetProperty("done", out var done) && done.ValueKind != JsonValueKind.True)
        {
            throw new JsonException("Incomplete model response.");
        }

        if (OptionalString(root, "done_reason") == "length")
        {
            throw new JsonException("Truncated model response.");
        }

        if (!root.TryGetProperty("message", out var message))
        {
            throw new JsonException("Missing assistant message.");
        }

        RequireObject(message);
        if (OptionalString(message, "role") != ChatModelRoles.Assistant)
        {
            throw new JsonException("Invalid assistant role.");
        }

        var content = OptionalString(message, "content");
        var allowedNames = (request.Tools ?? []).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var usedIds = request.Messages.SelectMany(m => m.ToolCalls ?? [])
            .Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        var calls = new List<ChatModelToolCall>();
        if (message.TryGetProperty("tool_calls", out var nativeCalls)
            && nativeCalls.ValueKind != JsonValueKind.Null)
        {
            if (nativeCalls.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Invalid tool calls.");
            }

            foreach (var call in nativeCalls.EnumerateArray())
            {
                RequireObject(call);
                if (!call.TryGetProperty("function", out var function))
                {
                    throw new JsonException("Missing tool function.");
                }

                calls.Add(ParseCall(function, OptionalString(call, "id"), allowedNames, usedIds));
            }
        }

        if (calls.Count == 0 && RecoverInlineCall(content, allowedNames, usedIds) is { } inlineCall)
        {
            calls.Add(inlineCall);
            content = null;
        }

        if (calls.Count == 0 && string.IsNullOrWhiteSpace(content))
        {
            throw new JsonException("Empty assistant output.");
        }

        var input = OptionalTokenCount(root, "prompt_eval_count");
        var output = OptionalTokenCount(root, "eval_count");
        if (input.HasValue && output.HasValue && input.Value > long.MaxValue - output.Value)
        {
            throw new JsonException("Invalid token usage.");
        }

        return new ChatModelResponse(
            new ChatModelMessage(ChatModelRoles.Assistant, content, calls),
            resolvedModel: OptionalString(root, "model"),
            usage: input.HasValue || output.HasValue
                ? new ChatModelUsage(input, output, input + output)
                : null);
    }

    private static ChatModelToolCall? RecoverInlineCall(
        string? content,
        IReadOnlySet<string> allowedNames,
        ISet<string> usedIds)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || !content[start..].Contains("\"name\"", StringComparison.Ordinal)) return null;
        if (end <= start) throw new JsonException("Incomplete inline tool call.");

        using var document = JsonDocument.Parse(content[start..(end + 1)]);
        var function = document.RootElement;
        if (!function.TryGetProperty("name", out _)) return null;
        return ParseCall(function, null, allowedNames, usedIds, inline: true);
    }

    private static ChatModelToolCall ParseCall(
        JsonElement function,
        string? id,
        IReadOnlySet<string> allowedNames,
        ISet<string> usedIds,
        bool inline = false)
    {
        RequireObject(function);
        var name = OptionalString(function, "name");
        if (string.IsNullOrWhiteSpace(name) || !allowedNames.Contains(name))
        {
            throw new JsonException("Unrecognized tool name.");
        }

        if (!function.TryGetProperty("arguments", out var arguments)
            && !(inline && function.TryGetProperty("parameters", out arguments)))
        {
            throw new JsonException("Missing tool arguments.");
        }

        RequireObject(arguments);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || !names.Add(property.Name))
            {
                throw new JsonException("Invalid tool argument names.");
            }
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            do { id = $"ollama_{Guid.NewGuid():N}"; } while (!usedIds.Add(id));
        }
        else if (id != id.Trim() || !usedIds.Add(id))
        {
            throw new JsonException("Invalid or duplicate tool-call ID.");
        }

        return new ChatModelToolCall(id, name, arguments);
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a JSON object.");
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JsonException("Expected a JSON string.");
        return value.GetString();
    }

    private static long? OptionalTokenCount(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0)
            throw new JsonException("Invalid token count.");
        return count;
    }

    private static ChatModelFailureCategory Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => ChatModelFailureCategory.InvalidRequest,
        HttpStatusCode.Unauthorized => ChatModelFailureCategory.Authentication,
        HttpStatusCode.Forbidden => ChatModelFailureCategory.PolicyRejected,
        HttpStatusCode.NotFound => ChatModelFailureCategory.LocalModelUnavailable,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ChatModelFailureCategory.Timeout,
        HttpStatusCode.TooManyRequests => ChatModelFailureCategory.RateLimited,
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            => ChatModelFailureCategory.ProviderUnavailable,
        _ => ChatModelFailureCategory.UnknownProviderError
    };

    private static ChatModelException Failure(ChatModelFailureCategory category, string model, int? status = null) =>
        new(category, new ChatModelFailureMetadata(
            provider: ChatModelProviders.Ollama, requestedModel: model, httpStatusCode: status));

    private sealed record ChatRequestPayload(
        string Model, IReadOnlyList<MessagePayload> Messages, IReadOnlyList<ToolPayload>? Tools,
        GenerationOptions Options, bool Stream = false, bool Think = false);
    private sealed record GenerationOptions(int NumPredict);
    private sealed record MessagePayload(
        string Role, string Content, IReadOnlyList<CallPayload>? ToolCalls, string? ToolName);
    private sealed record CallPayload(FunctionPayload Function);
    private sealed record FunctionPayload(string Name, IReadOnlyDictionary<string, JsonElement> Arguments);
    private sealed record ToolPayload(string Type, ToolDefinitionPayload Function);
    private sealed record ToolDefinitionPayload(string Name, string Description, JsonElement Parameters);
}
