using System.Collections.ObjectModel;
using ai_assistant_service.Configuration;
using ai_assistant_service.Services.Contracts;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Services.ChatModels;

/// <summary>
/// Converts the validated task-map options into an immutable runtime snapshot.
/// Keeping this snapshot separate from configuration binding prevents model,
/// provider, and budget changes from affecting requests until the service restarts.
/// </summary>
public sealed class TaskModelProfileCatalog
{
    private readonly IReadOnlyDictionary<string, TaskModelProfile> _profiles;

    public TaskModelProfileCatalog(IOptions<AiModelsOptions> options)
    {
        _profiles = new ReadOnlyDictionary<string, TaskModelProfile>(
            options.Value.Tasks.ToDictionary(
                pair => pair.Key,
                pair => CreateProfile(pair.Key, pair.Value),
                StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, TaskModelProfile> Profiles => _profiles;

    public bool TryGet(string taskId, out TaskModelProfile profile) =>
        _profiles.TryGetValue(taskId, out profile!);

    public TaskModelProfile GetRequired(string taskId) =>
        TryGet(taskId, out var profile)
            ? profile
            : throw new ChatModelException(ChatModelFailureCategory.InvalidConfiguration);

    private static TaskModelProfile CreateProfile(string taskId, TaskModelOptions options)
    {
        ChatModelProviders.TryNormalize(options.Provider, out var provider);
        return new TaskModelProfile(
            taskId,
            provider,
            options.Model!,
            options.TimeoutSeconds!.Value,
            options.RequestTimeoutSeconds!.Value,
            options.MaxOutputTokens!.Value);
    }
}

/// <summary>
/// Associates one independently registered provider adapter with its canonical
/// provider name. This avoids selecting a global client or mutating shared client
/// state when different tasks use different providers.
/// </summary>
public sealed record ChatModelClientRegistration
{
    public ChatModelClientRegistration(string provider, IChatModelClient client)
    {
        if (!ChatModelProviders.TryNormalize(provider, out var normalizedProvider))
        {
            throw new ArgumentException("The provider registration is not supported.", nameof(provider));
        }

        Provider = normalizedProvider;
        Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Provider { get; }
    public IChatModelClient Client { get; }
}

/// <summary>
/// Resolves an explicit backend task ID to its configured profile and provider
/// adapter. It deliberately has no fallback behavior, so an unknown task or
/// unavailable adapter fails before any model request is sent.
/// </summary>
public sealed class TaskChatModelResolver : ITaskChatModelResolver
{
    private readonly TaskModelProfileCatalog _catalog;
    private readonly IReadOnlyDictionary<string, IChatModelClient> _clients;

    public TaskChatModelResolver(
        TaskModelProfileCatalog catalog,
        IEnumerable<ChatModelClientRegistration> registrations)
    {
        _catalog = catalog;

        var clients = new Dictionary<string, IChatModelClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registrations)
        {
            if (!clients.TryAdd(registration.Provider, registration.Client))
            {
                throw new InvalidOperationException(
                    $"Multiple chat model clients are registered for provider '{registration.Provider}'.");
            }
        }

        _clients = new ReadOnlyDictionary<string, IChatModelClient>(clients);
    }

    public ITaskChatModelClient Resolve(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || !_catalog.TryGet(taskId, out var profile))
        {
            throw new ChatModelException(ChatModelFailureCategory.InvalidConfiguration);
        }

        if (!_clients.TryGetValue(profile.Provider, out var client))
        {
            throw new ChatModelException(
                ChatModelFailureCategory.InvalidConfiguration,
                new ChatModelFailureMetadata(
                    provider: profile.Provider,
                    requestedModel: profile.Model));
        }

        return new TaskChatModelClient(profile, client);
    }
}

/// <summary>
/// Binds a single immutable task profile to a provider adapter. It ensures callers
/// cannot override the selected model or output limit and applies a fresh
/// per-round timeout without changing shared adapter or HTTP client settings.
/// </summary>
public sealed class TaskChatModelClient : ITaskChatModelClient
{
    private readonly IChatModelClient _client;

    public TaskChatModelClient(TaskModelProfile profile, IChatModelClient client)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public TaskModelProfile Profile { get; }

    public async Task<ChatModelResponse> ChatAsync(
        IReadOnlyList<ChatModelMessage> messages,
        IReadOnlyList<ChatModelTool>? tools,
        CancellationToken cancellationToken)
    {
        using var roundTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(Profile.TimeoutSeconds));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            roundTimeout.Token);

        var request = new ChatModelRequest(
            Profile.Model,
            messages,
            tools,
            Profile.MaxOutputTokens,
            TimeSpan.FromSeconds(Profile.TimeoutSeconds));

        try
        {
            return await _client.ChatAsync(request, linkedCancellation.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested
                  && roundTimeout.IsCancellationRequested)
        {
            throw new ChatModelException(
                ChatModelFailureCategory.Timeout,
                new ChatModelFailureMetadata(
                    provider: Profile.Provider,
                    requestedModel: Profile.Model));
        }
    }
}

/// <summary>
/// Creates the request-wide deadline that spans prompt preparation, model rounds,
/// and tool execution. It is separate from the per-round timeout so orchestration
/// can enforce one non-resetting end-to-end budget for the entire request.
/// </summary>
public static class TaskModelRequestDeadline
{
    public static CancellationTokenSource Create(
        TaskModelProfile profile,
        CancellationToken callerCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(profile.RequestTimeoutSeconds));
        return requestTimeout;
    }
}
