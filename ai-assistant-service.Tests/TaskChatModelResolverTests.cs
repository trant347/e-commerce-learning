using System.Collections.Concurrent;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.ChatModels;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace ai_assistant_service.Tests;

public class TaskChatModelResolverTests
{
    [Fact]
    public async Task SyntheticTasks_UseIndependentProfilesOnOneAdapter()
    {
        var adapter = new RecordingChatModelClient();
        var resolver = CreateResolver(
            new Dictionary<string, TaskModelOptions>
            {
                ["chat"] = Profile("model-chat", 128),
                ["category_resolution"] = Profile("model-category", 32)
            },
            adapter);

        var chat = resolver.Resolve("chat");
        var category = resolver.Resolve("category_resolution");
        var messages = new[] { new ChatModelMessage(ChatModelRoles.User, "hello") };

        await Task.WhenAll(
            chat.ChatAsync(messages, tools: null, CancellationToken.None),
            category.ChatAsync(messages, tools: null, CancellationToken.None));

        Assert.Contains(
            adapter.Requests,
            request => request.Model == "model-chat" && request.MaxOutputTokens == 128);
        Assert.Contains(
            adapter.Requests,
            request => request.Model == "model-category" && request.MaxOutputTokens == 32);
        Assert.Equal("model-chat", chat.Profile.Model);
        Assert.Equal("model-category", category.Profile.Model);
    }

    [Fact]
    public async Task MixedProviderTasks_ResolveIndependentAdapters()
    {
        var ollama = new RecordingChatModelClient();
        var openRouter = new RecordingChatModelClient();
        var catalog = new TaskModelProfileCatalog(
            Options.Create(new AiModelsOptions
            {
                Tasks = new Dictionary<string, TaskModelOptions>
                {
                    ["chat"] = Profile("ollama-model", 128),
                    ["synthetic"] = Profile(
                        "openrouter-model",
                        32,
                        provider: ChatModelProviders.OpenRouter)
                }
            }));
        var resolver = new TaskChatModelResolver(
            catalog,
            [
                new ChatModelClientRegistration(ChatModelProviders.Ollama, ollama),
                new ChatModelClientRegistration(ChatModelProviders.OpenRouter, openRouter)
            ]);
        var messages = new[] { new ChatModelMessage(ChatModelRoles.User, "hello") };

        await resolver.Resolve("chat").ChatAsync(messages, null, CancellationToken.None);
        await resolver.Resolve("synthetic").ChatAsync(messages, null, CancellationToken.None);

        Assert.Equal("ollama-model", Assert.Single(ollama.Requests).Model);
        Assert.Equal("openrouter-model", Assert.Single(openRouter.Requests).Model);
    }

    [Fact]
    public void UnknownTask_FailsWithoutFallingBackOrCallingProvider()
    {
        var adapter = new RecordingChatModelClient();
        var resolver = CreateResolver(
            new Dictionary<string, TaskModelOptions>
            {
                ["chat"] = Profile("model-chat", 128)
            },
            adapter);

        var exception = Assert.Throws<ChatModelException>(
            () => resolver.Resolve("does_not_exist"));

        Assert.Equal(ChatModelFailureCategory.InvalidConfiguration, exception.Category);
        Assert.Empty(adapter.Requests);
    }

    [Fact]
    public async Task RoundDeadline_ProducesTypedTimeout()
    {
        var adapter = new RecordingChatModelClient(waitForCancellation: true);
        var resolver = CreateResolver(
            new Dictionary<string, TaskModelOptions>
            {
                ["chat"] = Profile("slow-model", 128, timeoutSeconds: 1)
            },
            adapter);
        var client = resolver.Resolve("chat");

        var exception = await Assert.ThrowsAsync<ChatModelException>(
            () => client.ChatAsync(
                [new ChatModelMessage(ChatModelRoles.User, "hello")],
                tools: null,
                CancellationToken.None));

        Assert.Equal(ChatModelFailureCategory.Timeout, exception.Category);
    }

    [Fact]
    public async Task CallerCancellation_EscapesInsteadOfBecomingTimeout()
    {
        var adapter = new RecordingChatModelClient(waitForCancellation: true);
        var resolver = CreateResolver(
            new Dictionary<string, TaskModelOptions>
            {
                ["chat"] = Profile("slow-model", 128, timeoutSeconds: 10)
            },
            adapter);
        var client = resolver.Resolve("chat");
        using var callerCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ChatAsync(
                [new ChatModelMessage(ChatModelRoles.User, "hello")],
                tools: null,
                callerCancellation.Token));
    }

    [Fact]
    public async Task RequestDeadline_IsSeparateFromRoundDeadline()
    {
        var profile = new TaskModelProfile(
            "chat",
            "Ollama",
            "model",
            timeoutSeconds: 1,
            requestTimeoutSeconds: 2,
            maxOutputTokens: 128);
        using var deadline = TaskModelRequestDeadline.Create(
            profile,
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token));
    }

    private static TaskChatModelResolver CreateResolver(
        Dictionary<string, TaskModelOptions> profiles,
        IChatModelClient adapter)
    {
        var catalog = new TaskModelProfileCatalog(
            Options.Create(new AiModelsOptions { Tasks = profiles }));
        return new TaskChatModelResolver(
            catalog,
            [new ChatModelClientRegistration(ChatModelProviders.Ollama, adapter)]);
    }

    private static TaskModelOptions Profile(
        string model,
        int maxOutputTokens,
        int timeoutSeconds = 10,
        string provider = ChatModelProviders.Ollama) =>
        new()
        {
            Provider = provider,
            Model = model,
            TimeoutSeconds = timeoutSeconds,
            RequestTimeoutSeconds = 30,
            MaxOutputTokens = maxOutputTokens
        };

    private sealed class RecordingChatModelClient : IChatModelClient
    {
        private readonly bool _waitForCancellation;

        public RecordingChatModelClient(bool waitForCancellation = false)
        {
            _waitForCancellation = waitForCancellation;
        }

        public ConcurrentQueue<ChatModelRequest> Requests { get; } = new();

        public async Task<ChatModelResponse> ChatAsync(
            ChatModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);

            if (_waitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new ChatModelResponse(
                new ChatModelMessage(ChatModelRoles.Assistant, "ok"));
        }
    }
}
