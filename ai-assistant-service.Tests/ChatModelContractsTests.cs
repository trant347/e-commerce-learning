using System.Text.Json;
using ai_assistant_service.Services.Contracts;
using Xunit;

namespace ai_assistant_service.Tests;

public class ChatModelContractsTests
{
    [Fact]
    public void ToolAndToolCall_CloneJsonValuesBeyondDocumentLifetime()
    {
        ChatModelTool tool;
        ChatModelToolCall call;

        using (var document = JsonDocument.Parse(
                   """
                   {
                     "schema": {
                       "type": "object",
                       "properties": {
                         "category": { "type": "string" }
                       }
                     },
                     "arguments": {
                       "category": "landscaping",
                       "maxRate": 75,
                       "available": true
                     }
                   }
                   """))
        {
            tool = new ChatModelTool(
                "search_task_masters",
                "Search available task masters.",
                document.RootElement.GetProperty("schema"));
            call = new ChatModelToolCall(
                "call-1",
                "search_task_masters",
                document.RootElement.GetProperty("arguments"));
        }

        Assert.Equal(
            "string",
            tool.Parameters
                .GetProperty("properties")
                .GetProperty("category")
                .GetProperty("type")
                .GetString());
        Assert.Equal("landscaping", call.Arguments["category"].GetString());
        Assert.Equal(75, call.Arguments["maxRate"].GetInt32());
        Assert.True(call.Arguments["available"].GetBoolean());
    }

    [Fact]
    public void Messages_RepresentMultipleCallsAndMatchingToolResults()
    {
        var firstCall = ToolCall("call-1", "search_task_masters");
        var secondCall = ToolCall("call-2", "get_bookings");
        var calls = new List<ChatModelToolCall> { firstCall, secondCall };

        var assistant = new ChatModelMessage(
            ChatModelRoles.Assistant,
            content: null,
            toolCalls: calls);
        var firstResult = new ChatModelMessage(
            ChatModelRoles.Tool,
            """{"name":"Alice"}""",
            toolCallId: firstCall.Id);
        var secondResult = new ChatModelMessage(
            ChatModelRoles.Tool,
            "[]",
            toolCallId: secondCall.Id);

        calls.Clear();

        Assert.Equal(["call-1", "call-2"], assistant.ToolCalls!.Select(call => call.Id));
        Assert.Equal(firstCall.Id, firstResult.ToolCallId);
        Assert.Equal(secondCall.Id, secondResult.ToolCallId);
    }

    [Fact]
    public void Request_SnapshotsMessagesAndTools()
    {
        var messages = new List<ChatModelMessage>
        {
            new(ChatModelRoles.User, "Find a landscaper.")
        };
        var tools = new List<ChatModelTool>
        {
            Tool("search_task_masters")
        };

        var request = new ChatModelRequest("model-a", messages, tools, 512);
        messages.Clear();
        tools.Clear();

        Assert.Single(request.Messages);
        Assert.Single(request.Tools!);
    }

    [Fact]
    public void RequestsAndProfiles_RejectInvalidBudgets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatModelRequest(
                "model",
                [new ChatModelMessage(ChatModelRoles.User, "hello")],
                tools: null,
                maxOutputTokens: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TaskModelProfile("chat", "Ollama", "qwen3:8b", 60, 30, 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TaskModelProfile("chat", "Ollama", "qwen3:8b", 60, 300, 0));
    }

    [Fact]
    public void Contracts_RejectNonObjectSchemasAndArguments()
    {
        using var document = JsonDocument.Parse("""{"scalar":"not-an-object"}""");
        var scalar = document.RootElement.GetProperty("scalar");

        Assert.Throws<ArgumentException>(() =>
            new ChatModelTool("search", "Search.", scalar));
        Assert.Throws<ArgumentException>(() =>
            new ChatModelToolCall("call-1", "search", scalar));
    }

    [Fact]
    public void Message_RejectsInvalidRoleSpecificState()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChatModelMessage(ChatModelRoles.Tool, "result"));
        Assert.Throws<ArgumentException>(() =>
            new ChatModelMessage(
                ChatModelRoles.User,
                "hello",
                toolCalls: [ToolCall("call-1", "search")]));
        Assert.Throws<ArgumentException>(() =>
            new ChatModelMessage(ChatModelRoles.Assistant, content: null));
    }

    [Fact]
    public void FailureException_DoesNotIncludeDiagnosticMetadataInMessage()
    {
        var metadata = new ChatModelFailureMetadata(
            provider: "OpenRouter",
            requestedModel: "sentinel-model",
            resolvedModel: "vendor/model-version",
            httpStatusCode: 429,
            providerErrorCode: 429,
            providerRequestId: "sentinel-request-id",
            retryAfter: TimeSpan.FromSeconds(2),
            limitSource: "sentinel-limit-source");

        var exception = new ChatModelException(
            ChatModelFailureCategory.RateLimited,
            metadata);

        Assert.Equal(
            "Chat model request failed with category 'RateLimited'.",
            exception.Message);
        Assert.DoesNotContain("sentinel-model", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel-request-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel-limit-source", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureMetadata_RejectsInvalidStatusAndRetryDelay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatModelFailureMetadata(httpStatusCode: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatModelFailureMetadata(retryAfter: TimeSpan.FromSeconds(-1)));
    }

    private static ChatModelTool Tool(string name)
    {
        using var document = JsonDocument.Parse("""{"type":"object"}""");
        return new ChatModelTool(name, "Test tool.", document.RootElement);
    }

    private static ChatModelToolCall ToolCall(string id, string name)
    {
        using var document = JsonDocument.Parse("""{"category":"landscaping"}""");
        return new ChatModelToolCall(id, name, document.RootElement);
    }
}
