using System.Diagnostics;
using System.Diagnostics.Metrics;
using ai_assistant_service.Services.Contracts;

namespace ai_assistant_service.Services.Telemetry;

/// <summary>
/// Metadata-only traces and metrics for assistant chat requests and model rounds.
/// Tags come only from validated task profiles, bounded enums, registered tool
/// names, and provider metadata. Prompts, arguments, results, completions, user
/// identifiers, and credentials are never recorded. Request/generation IDs and
/// resolved models are trace attributes only, never metric dimensions.
/// </summary>
public static class AiAssistantTelemetry
{
    public const string Name = "TaskMaster.AiAssistant";

    public const string ChatActivityName = "ai.chat";
    public const string ModelRoundActivityName = "ai.model.round";
    public const string RetryEventName = "ai.model.retry";

    public const string ChatDurationMetric = "ai_assistant.chat.duration";
    public const string ModelRoundDurationMetric = "ai_assistant.model_round.duration";
    public const string FailuresMetric = "ai_assistant.failures";
    public const string RetriesMetric = "ai_assistant.model.retries";
    public const string ToolCallsMetric = "ai_assistant.tool_calls";
    public const string TokensMetric = "ai_assistant.model.tokens";

    public const string TaskTag = "ai.task";
    public const string ProviderTag = "ai.model.provider";
    public const string RequestedModelTag = "ai.model.requested_model";
    public const string ResolvedModelTag = "ai.model.resolved_model";
    public const string ProviderRequestIdTag = "ai.model.provider_request_id";
    public const string OutcomeTag = "ai.outcome";
    public const string ErrorTypeTag = "error.type";
    public const string HttpStatusTag = "http.response.status_code";
    public const string RoundNumberTag = "ai.round.number";
    public const string RoundToolCallsTag = "ai.round.tool_calls";
    public const string RetryCountTag = "ai.model.retry_count";
    public const string RetryNumberTag = "ai.model.retry_number";
    public const string RetryDelayTag = "ai.model.retry_delay_ms";
    public const string ChatRoundsTag = "ai.chat.rounds";
    public const string ChatToolCallsTag = "ai.chat.tool_calls";
    public const string ChatRoundLimitTag = "ai.chat.round_limit_reached";
    public const string ToolNameTag = "ai.tool.name";
    public const string TokenTypeTag = "ai.token.type";
    public const string InputTokensTag = "ai.usage.input_tokens";
    public const string OutputTokensTag = "ai.usage.output_tokens";
    public const string TotalTokensTag = "ai.usage.total_tokens";

    public const string Success = "success";
    public const string Failure = "failure";
    public const string Canceled = "canceled";
    public const string UnregisteredTool = "unregistered";
    public const string UnexpectedError = "unexpected";

    public static readonly ActivitySource ActivitySource = new(Name);
    public static readonly Meter Meter = new(Name);

    private static readonly Histogram<double> ChatDuration = Meter.CreateHistogram<double>(
        ChatDurationMetric, "s", "End-to-end assistant chat request duration.");
    private static readonly Histogram<double> ModelRoundDuration = Meter.CreateHistogram<double>(
        ModelRoundDurationMetric, "s", "Duration of one model round, including adapter retries.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        FailuresMetric, "{failure}", "Assistant chat requests that returned a handled failure.");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        RetriesMetric, "{retry}", "Model request retries performed by provider adapters.");
    private static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>(
        ToolCallsMetric, "{call}", "Tool calls executed for assistant requests.");
    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        TokensMetric, "{token}", "Model token usage reported by the provider.");

    private static readonly AsyncLocal<ModelRoundTelemetry?> CurrentRound = new();

    public static ChatTelemetry StartChat(TaskModelProfile profile, CancellationToken callerToken) =>
        new(profile, callerToken);

    public static ModelRoundTelemetry StartModelRound(
        TaskModelProfile profile,
        int roundNumber,
        CancellationToken callerToken) =>
        new(profile, roundNumber, callerToken);

    /// <summary>
    /// Records one adapter retry. The task label comes from the enclosing validated
    /// model-round scope; without one, the retry is recorded without a task label.
    /// </summary>
    public static void RecordRetry(
        string provider,
        string requestedModel,
        ChatModelFailureCategory category,
        int? httpStatusCode,
        int retryNumber,
        TimeSpan delay)
    {
        var round = CurrentRound.Value;
        var tags = round?.ProfileTags() ?? new TagList
        {
            { ProviderTag, provider },
            { RequestedModelTag, requestedModel }
        };
        tags.Add(ErrorTypeTag, category.ToString());
        Retries.Add(1, tags);

        if (round is null)
        {
            return;
        }

        round.RetryCount = retryNumber;
        round.Activity?.AddEvent(new ActivityEvent(RetryEventName, tags: new ActivityTagsCollection
        {
            { ErrorTypeTag, category.ToString() },
            { HttpStatusTag, httpStatusCode },
            { RetryNumberTag, retryNumber },
            { RetryDelayTag, delay.TotalMilliseconds }
        }));
    }

    private static TagList ProfileTagList(TaskModelProfile profile) => new()
    {
        { TaskTag, profile.TaskId },
        { ProviderTag, profile.Provider },
        { RequestedModelTag, profile.Model }
    };

    private static Activity? StartActivity(string name, TaskModelProfile profile)
    {
        var activity = ActivitySource.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag(TaskTag, profile.TaskId);
        activity?.SetTag(ProviderTag, profile.Provider);
        activity?.SetTag(RequestedModelTag, profile.Model);
        return activity;
    }

    private static void MarkFailure(Activity? activity, string errorType, int? httpStatusCode)
    {
        activity?.SetTag(OutcomeTag, Failure);
        activity?.SetTag(ErrorTypeTag, errorType);
        activity?.SetTag(HttpStatusTag, httpStatusCode);
        activity?.SetStatus(ActivityStatusCode.Error, errorType);
    }

    private static string CompleteOutcome(
        Activity? activity,
        CancellationToken callerToken,
        ref string? errorType)
    {
        if (callerToken.IsCancellationRequested)
        {
            activity?.SetTag(OutcomeTag, Canceled);
            return Canceled;
        }

        errorType = UnexpectedError;
        MarkFailure(activity, UnexpectedError, httpStatusCode: null);
        return Failure;
    }

    /// <summary>Telemetry for one end-to-end chat request.</summary>
    public sealed class ChatTelemetry : IDisposable
    {
        private readonly TaskModelProfile _profile;
        private readonly CancellationToken _callerToken;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly Activity? _activity;
        private string? _outcome;
        private string? _errorType;
        private int _rounds;
        private int _toolCalls;

        internal ChatTelemetry(TaskModelProfile profile, CancellationToken callerToken)
        {
            _profile = profile;
            _callerToken = callerToken;
            _activity = StartActivity(ChatActivityName, profile);
        }

        public void RoundStarted() => _rounds++;

        /// <param name="toolName">A registered tool name or <see cref="UnregisteredTool"/>.</param>
        public void ToolCallCompleted(string toolName, bool succeeded)
        {
            _toolCalls++;
            var tags = ProfileTagList(_profile);
            tags.Add(ToolNameTag, toolName);
            tags.Add(OutcomeTag, succeeded ? Success : Failure);
            ToolCalls.Add(1, tags);
        }

        public void Succeeded(bool roundLimitReached)
        {
            _outcome = Success;
            _activity?.SetTag(OutcomeTag, Success);
            _activity?.SetTag(ChatRoundLimitTag, roundLimitReached);
        }

        public void Failed(ChatModelFailureCategory category, int? httpStatusCode)
        {
            _outcome = Failure;
            _errorType = category.ToString();
            MarkFailure(_activity, _errorType, httpStatusCode);
            var tags = ProfileTagList(_profile);
            tags.Add(ErrorTypeTag, _errorType);
            Failures.Add(1, tags);
        }

        public void Dispose()
        {
            _outcome ??= CompleteOutcome(_activity, _callerToken, ref _errorType);
            _activity?.SetTag(ChatRoundsTag, _rounds);
            _activity?.SetTag(ChatToolCallsTag, _toolCalls);

            var tags = ProfileTagList(_profile);
            tags.Add(OutcomeTag, _outcome);
            if (_errorType is not null)
            {
                tags.Add(ErrorTypeTag, _errorType);
            }

            ChatDuration.Record(Stopwatch.GetElapsedTime(_startedAt).TotalSeconds, tags);
            _activity?.Dispose();
        }
    }

    /// <summary>
    /// Telemetry for one model round. While active, it is the ambient round so
    /// provider adapters can record retries against the validated task profile.
    /// </summary>
    public sealed class ModelRoundTelemetry : IDisposable
    {
        private readonly TaskModelProfile _profile;
        private readonly CancellationToken _callerToken;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly ModelRoundTelemetry? _previous;
        private string? _outcome;
        private string? _errorType;
        private int? _httpStatusCode;

        internal ModelRoundTelemetry(
            TaskModelProfile profile,
            int roundNumber,
            CancellationToken callerToken)
        {
            _profile = profile;
            _callerToken = callerToken;
            Activity = StartActivity(ModelRoundActivityName, profile);
            Activity?.SetTag(RoundNumberTag, roundNumber);
            _previous = CurrentRound.Value;
            CurrentRound.Value = this;
        }

        internal Activity? Activity { get; }
        internal int RetryCount { get; set; }

        internal TagList ProfileTags() => ProfileTagList(_profile);

        public void Succeeded(ChatModelResponse response)
        {
            _outcome = Success;
            Activity?.SetTag(OutcomeTag, Success);
            Activity?.SetTag(ResolvedModelTag, response.ResolvedModel);
            Activity?.SetTag(ProviderRequestIdTag, response.ProviderRequestId);
            Activity?.SetTag(RoundToolCallsTag, response.Message.ToolCalls?.Count ?? 0);
            RecordUsage(response.Usage);
        }

        public void Failed(ChatModelException exception)
        {
            _outcome = Failure;
            _errorType = exception.Category.ToString();
            _httpStatusCode = exception.Metadata.HttpStatusCode;
            MarkFailure(Activity, _errorType, _httpStatusCode);
            Activity?.SetTag(ResolvedModelTag, exception.Metadata.ResolvedModel);
            Activity?.SetTag(ProviderRequestIdTag, exception.Metadata.ProviderRequestId);
        }

        public void Dispose()
        {
            if (ReferenceEquals(CurrentRound.Value, this))
            {
                CurrentRound.Value = _previous;
            }

            _outcome ??= CompleteOutcome(Activity, _callerToken, ref _errorType);
            Activity?.SetTag(RetryCountTag, RetryCount);

            var tags = ProfileTagList(_profile);
            tags.Add(OutcomeTag, _outcome);
            if (_errorType is not null)
            {
                tags.Add(ErrorTypeTag, _errorType);
            }

            if (_httpStatusCode is not null)
            {
                tags.Add(HttpStatusTag, _httpStatusCode.Value);
            }

            ModelRoundDuration.Record(Stopwatch.GetElapsedTime(_startedAt).TotalSeconds, tags);
            Activity?.Dispose();
        }

        private void RecordUsage(ChatModelUsage? usage)
        {
            if (usage is null)
            {
                return;
            }

            Activity?.SetTag(InputTokensTag, usage.InputTokens);
            Activity?.SetTag(OutputTokensTag, usage.OutputTokens);
            Activity?.SetTag(TotalTokensTag, usage.TotalTokens);
            AddTokens("input", usage.InputTokens);
            AddTokens("output", usage.OutputTokens);
            AddTokens("total", usage.TotalTokens);
        }

        private void AddTokens(string tokenType, long? count)
        {
            if (count is null)
            {
                return;
            }

            var tags = ProfileTagList(_profile);
            tags.Add(TokenTypeTag, tokenType);
            Tokens.Add(count.Value, tags);
        }
    }
}
