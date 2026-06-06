using System.Net;
using System.Text;
using System.Text.Json;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ai_assistant_service.Tests;

/// <summary>
/// Unit tests for <see cref="OllamaClient"/>. The HttpClient is wired to a
/// <see cref="CapturingHandler"/> so we can inspect the exact JSON payload
/// the client sends to Ollama without spinning up a real server.
/// </summary>
public class OllamaClientTests
{
    private static readonly OllamaChatMessage UserMsg = new()
    {
        Role = "user",
        Content = "find me a dog walker"
    };

    /// <summary>
    /// Ensures the chat payload always contains "think": false so qwen3 (and
    /// other reasoning-enabled models) skip their slow internal &lt;think&gt;
    /// step that previously caused timeouts before any tool call was emitted.
    /// </summary>
    [Fact]
    public async Task ChatAsync_AlwaysSendsThinkFalse_ToAvoidTimeouts()
    {
        var (client, handler) = BuildClient(StubAssistantResponse("hi"));

        await client.ChatAsync("qwen3:8b", new[] { UserMsg }, tools: null, CancellationToken.None);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("think", out var thinkProp),
            "Expected 'think' property in Ollama chat payload");
        Assert.Equal(JsonValueKind.False, thinkProp.ValueKind);
    }

    [Fact]
    public async Task ChatAsync_SendsModelMessagesAndStreamFalse()
    {
        var (client, handler) = BuildClient(StubAssistantResponse("ok"));

        await client.ChatAsync("qwen3:8b", new[] { UserMsg }, tools: null, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;

        Assert.Equal("qwen3:8b", root.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.False, root.GetProperty("stream").ValueKind);

        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("find me a dog walker", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatAsync_OmitsToolsArray_WhenNoToolsProvided()
    {
        // Null tools should be omitted (DefaultIgnoreCondition.WhenWritingNull),
        // not serialized as `"tools": null`.
        var (client, handler) = BuildClient(StubAssistantResponse("ok"));

        await client.ChatAsync("qwen3:8b", new[] { UserMsg }, tools: null, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("tools", out _),
            "Expected 'tools' to be omitted when null");
    }

    [Fact]
    public async Task ChatAsync_IncludesToolsArray_WhenToolsProvided()
    {
        var tools = new List<object>
        {
            new
            {
                type = "function",
                function = new { name = "search_task_masters", description = "search", parameters = new { } }
            }
        };
        var (client, handler) = BuildClient(StubAssistantResponse("ok"));

        await client.ChatAsync("qwen3:8b", new[] { UserMsg }, tools, CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var toolsArr = doc.RootElement.GetProperty("tools");
        Assert.Equal(1, toolsArr.GetArrayLength());
        Assert.Equal("search_task_masters",
            toolsArr[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChatAsync_ReturnsAssistantMessageFromOllama()
    {
        var (client, _) = BuildClient(StubAssistantResponse("Hello there!"));

        var msg = await client.ChatAsync("qwen3:8b", new[] { UserMsg }, null, CancellationToken.None);

        Assert.Equal("assistant", msg.Role);
        Assert.Equal("Hello there!", msg.Content);
    }

    [Fact]
    public async Task ChatAsync_OnHttpError_ReturnsFallbackMessageInsteadOfThrowing()
    {
        var (client, _) = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var msg = await client.ChatAsync("qwen3:8b", new[] { UserMsg }, null, CancellationToken.None);

        Assert.Equal("assistant", msg.Role);
        Assert.Contains("could not reach", msg.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private static (OllamaClient client, CapturingHandler handler) BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new CapturingHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama-test:11434") };
        var client = new OllamaClient(http, NullLogger<OllamaClient>.Instance);
        return (client, handler);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> StubAssistantResponse(string content)
    {
        var json = JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content }
        });
        return _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public string? LastRequestBody { get; private set; }

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return _responder(request);
        }
    }
}
