using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Clients;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ai_assistant_service.Tests;

public sealed class TaskRoutingIntegrationTests
{
    [Fact]
    public async Task ConcurrentRealAdapters_IsolateProfilesTranscriptsToolsAndCredentials()
    {
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Rendezvous(string model, CancellationToken token)
        {
            if (Interlocked.Increment(ref entered) == 3)
                allEntered.SetResult();
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        using var local = new CapturingHandler(false, Rendezvous);
        using var remote = new CapturingHandler(true, Rendezvous);
        var values = Configuration();
        AddProfile(values, "chat", "Ollama", "local-chat", 128, 5, 15);
        using var host = BuildHost(values, local, remote);
        await host.StartAsync();
        var resolver = host.Services.GetRequiredService<ITaskChatModelResolver>();
        var ids = new[] { "chat", "synthetic_a", "synthetic_b" };
        var clients = ids.Select(resolver.Resolve).ToArray();

        var responses = await Task.WhenAll(clients.Select(client => client.ChatAsync(
            [new ChatModelMessage("user", $"transcript-{client.Profile.TaskId}")],
            [new ChatModelTool($"tool_{client.Profile.TaskId}", "Synthetic tool.",
                JsonSerializer.SerializeToElement(new { type = "object" }))],
            CancellationToken.None)));

        Assert.Equal(3, entered);
        Assert.Single(local.Requests);
        Assert.Equal(2, remote.Requests.Count);
        for (var index = 0; index < clients.Length; index++)
        {
            var profile = clients[index].Profile;
            Assert.Equal($"answer-{profile.Model}", responses[index].Message.Content);
            Assert.Equal($"resolved-{profile.Model}", responses[index].ResolvedModel);
            var captured = Assert.Single(local.Requests.Concat(remote.Requests),
                item => item.Model == profile.Model);
            using var body = JsonDocument.Parse(captured.Body);
            var root = body.RootElement;
            Assert.Equal($"transcript-{profile.TaskId}",
                Assert.Single(root.GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());
            Assert.Equal($"tool_{profile.TaskId}", Assert.Single(root.GetProperty("tools").EnumerateArray())
                .GetProperty("function").GetProperty("name").GetString());
            Assert.Equal(profile.MaxOutputTokens, profile.Provider == "Ollama"
                ? root.GetProperty("options").GetProperty("num_predict").GetInt32()
                : root.GetProperty("max_tokens").GetInt32());
            Assert.Equal(profile.Provider == "Ollama" ? null : "fake-routing-key", captured.Credential);
            Assert.Equal(profile.Provider == "Ollama"
                ? "http://ollama.test:11434/api/chat"
                : "https://openrouter.test/api/v1/chat/completions", captured.Uri);
            foreach (var other in ids.Where(id => id != profile.TaskId))
            {
                Assert.DoesNotContain($"transcript-{other}", captured.Body);
                Assert.DoesNotContain($"tool_{other}\"", captured.Body);
            }
        }
        Assert.Equal([5, 10, 20], clients.Select(client => client.Profile.TimeoutSeconds));
        Assert.Equal([15, 30, 40], clients.Select(client => client.Profile.RequestTimeoutSeconds));
        await host.StopAsync();
    }

    [Fact]
    public async Task ConcurrentRoundTimeout_DoesNotCancelAnotherProviderOrChangeRouting()
    {
        var shortCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Delay(string model, CancellationToken token)
        {
            if (model == "local-chat")
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { shortCanceled.TrySetResult(); }
            }
            else
            {
                await shortCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
        }
        using var local = new CapturingHandler(false, Delay);
        using var remote = new CapturingHandler(true, Delay);
        using var host = BuildHost(Configuration(), local, remote);
        await host.StartAsync();
        var resolver = host.Services.GetRequiredService<ITaskChatModelResolver>();
        var chat = resolver.Resolve("chat");
        var synthetic = resolver.Resolve("synthetic_a");
        var slow = chat.ChatAsync([new ChatModelMessage("user", "slow")], null, CancellationToken.None);
        var healthy = synthetic.ChatAsync([new ChatModelMessage("user", "healthy")], null, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<ChatModelException>(() => slow);
        var response = await healthy;

        Assert.Equal(ChatModelFailureCategory.Timeout, failure.Category);
        Assert.Equal("answer-remote-a", response.Message.Content);
        Assert.Equal("local-chat", resolver.Resolve("chat").Profile.Model);
        Assert.Equal("remote-a", resolver.Resolve("synthetic_a").Profile.Model);
        Assert.Single(local.Requests);
        Assert.Single(remote.Requests);
        await host.StopAsync();
    }

    [Fact]
    public async Task UnknownTaskAndAuthenticationFailure_NeverFallbackOrSendOtherProfiles()
    {
        using var local = new CapturingHandler(false);
        using var remote = new CapturingHandler(true, status: HttpStatusCode.Unauthorized);
        using var host = BuildHost(Configuration(), local, remote);
        await host.StartAsync();
        var resolver = host.Services.GetRequiredService<ITaskChatModelResolver>();

        var unknown = Assert.Throws<ChatModelException>(() => resolver.Resolve("unknown"));
        Assert.Equal(ChatModelFailureCategory.InvalidConfiguration, unknown.Category);
        Assert.Empty(local.Requests);
        Assert.Empty(remote.Requests);
        var failure = await Assert.ThrowsAsync<ChatModelException>(() =>
            resolver.Resolve("synthetic_a").ChatAsync(
                [new ChatModelMessage("user", "private-transcript")], null, CancellationToken.None));

        Assert.Equal(ChatModelFailureCategory.Authentication, failure.Category);
        Assert.DoesNotContain("fake-routing-key", failure.ToString());
        Assert.Empty(local.Requests);
        Assert.Equal("remote-a", Assert.Single(remote.Requests).Model);
        Assert.Equal("remote-a", resolver.Resolve("synthetic_a").Profile.Model);
        await host.StopAsync();
    }

    [Fact]
    public async Task Restart_ChangesOnlyEditedProfileAndLeavesInFlightSelectionImmutable()
    {
        var values = Configuration();
        AddProfile(values, "chat", "Ollama", "local-chat", 128, 10, 30);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task HoldRequest(string model, CancellationToken token)
        {
            entered.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        using var local = new CapturingHandler(false, HoldRequest);
        using var remote = new CapturingHandler(true);
        using var original = BuildHost(values, local, remote);
        await original.StartAsync();
        var originalResolver = original.Services.GetRequiredService<ITaskChatModelResolver>();
        var boundChat = originalResolver.Resolve("chat");
        var inFlight = boundChat.ChatAsync(
            [new ChatModelMessage("user", "old-selection")], null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unchanged = originalResolver.Resolve("synthetic_a").Profile;
        var configuration = original.Services.GetRequiredService<IConfiguration>();
        AddProfile(values, "chat", "OpenRouter", "replacement-chat", 64, 15, 45);
        foreach (var (key, value) in values)
            configuration[key] = value;
        Assert.IsAssignableFrom<IConfigurationRoot>(configuration).Reload();

        using var restarted = BuildHost(values, local, remote);
        await restarted.StartAsync();
        var restartedResolver = restarted.Services.GetRequiredService<ITaskChatModelResolver>();
        Assert.Equal(boundChat.Profile, originalResolver.Resolve("chat").Profile);
        Assert.Equal(unchanged, restartedResolver.Resolve("synthetic_a").Profile);
        Assert.Equal(originalResolver.Resolve("synthetic_b").Profile,
            restartedResolver.Resolve("synthetic_b").Profile);
        var replacement = restartedResolver.Resolve("chat");
        Assert.Equal(new TaskModelProfile("chat", "OpenRouter", "replacement-chat", 15, 45, 64),
            replacement.Profile);

        release.SetResult();
        Assert.Equal("answer-local-chat", (await inFlight).Message.Content);
        await replacement.ChatAsync([new ChatModelMessage("user", "new-selection")], null, CancellationToken.None);
        Assert.Equal("local-chat", Assert.Single(local.Requests).Model);
        Assert.Equal("replacement-chat", Assert.Single(remote.Requests).Model);
        await restarted.StopAsync();
        await original.StopAsync();
    }

    private static IHost BuildHost(
        Dictionary<string, string?> values, CapturingHandler local, CapturingHandler remote) =>
        new HostBuilder()
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(values))
            .ConfigureServices((context, services) =>
            {
                services.AddAiModelRouting(context.Configuration);
                services.AddHttpClient<OllamaChatModelClient>().ConfigurePrimaryHttpMessageHandler(() => local);
                services.AddHttpClient<OpenRouterChatModelClient>().ConfigurePrimaryHttpMessageHandler(() => remote);
            }).Build();

    private static Dictionary<string, string?> Configuration()
    {
        var values = new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = "http://ollama.test:11434",
            ["OpenRouter:BaseUrl"] = "https://openrouter.test/api/v1",
            ["OpenRouter:ApiKey"] = "fake-routing-key",
            ["OpenRouter:MaxRetries"] = "0"
        };
        AddProfile(values, "chat", "Ollama", "local-chat", 128, 1, 5);
        AddProfile(values, "synthetic_a", "OpenRouter", "remote-a", 32, 10, 30);
        AddProfile(values, "synthetic_b", "OpenRouter", "remote-b", 16, 20, 40);
        return values;
    }

    private static void AddProfile(Dictionary<string, string?> values, string id, string provider,
        string model, int cap, int round, int request)
    {
        values[$"AiModels:Tasks:{id}:Provider"] = provider;
        values[$"AiModels:Tasks:{id}:Model"] = model;
        values[$"AiModels:Tasks:{id}:TimeoutSeconds"] = round.ToString(System.Globalization.CultureInfo.InvariantCulture);
        values[$"AiModels:Tasks:{id}:RequestTimeoutSeconds"] = request.ToString(System.Globalization.CultureInfo.InvariantCulture);
        values[$"AiModels:Tasks:{id}:MaxOutputTokens"] = cap.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record CapturedRequest(string Model, string Body, string? Credential, string Uri);

    private sealed class CapturingHandler(
        bool openRouter,
        Func<string, CancellationToken, Task>? delay = null,
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var parsed = JsonDocument.Parse(body);
            var model = parsed.RootElement.GetProperty("model").GetString()!;
            Requests.Enqueue(new(model, body, request.Headers.Authorization?.Parameter,
                request.RequestUri!.AbsoluteUri));
            if (delay is not null)
                await delay(model, cancellationToken);
            var message = new { role = "assistant", content = $"answer-{model}" };
            var response = status != HttpStatusCode.OK
                ? JsonSerializer.Serialize(new { error = new { code = (int)status, message = "private-error" } })
                : openRouter
                    ? JsonSerializer.Serialize(new { model = $"resolved-{model}",
                        choices = new[] { new { finish_reason = "stop", message } } })
                    : JsonSerializer.Serialize(new { model = $"resolved-{model}", message });
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
