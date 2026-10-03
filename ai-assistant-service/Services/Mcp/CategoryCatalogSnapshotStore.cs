using ai_assistant_service.Configuration;
using Microsoft.Extensions.Options;

namespace ai_assistant_service.Services.Mcp;

/// <summary>
/// Atomically publishes the last validated rich catalog and evaluates its readiness.
/// </summary>
public sealed class CategoryCatalogSnapshotStore
{
    public static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(5);

    private readonly CategoryResolutionOptions _options;
    private readonly TimeProvider _timeProvider;
    private CategoryCatalogSnapshot? _current;
    private RefreshFailure? _lastFailure;

    public CategoryCatalogSnapshotStore(
        IOptions<CategoryResolutionOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public bool IsRichCatalogRequired =>
        CategoryResolutionModes.IsRichCatalogMode(_options.Mode);

    public CategoryCatalogSnapshot? Current => Volatile.Read(ref _current);

    public void Publish(CategoryCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
        Volatile.Write(ref _lastFailure, null);
    }

    public void RecordFailure(string detail)
    {
        var safeDetail = string.IsNullOrWhiteSpace(detail)
            ? "Category catalog refresh failed."
            : detail;
        Volatile.Write(
            ref _lastFailure,
            new RefreshFailure(_timeProvider.GetUtcNow(), safeDetail));
    }

    public bool TryCaptureUsable(
        out CategoryCatalogSnapshot? snapshot,
        out string? unavailableReason)
    {
        snapshot = Current;
        unavailableReason = null;

        if (!IsRichCatalogRequired)
        {
            return true;
        }

        if (snapshot is null)
        {
            unavailableReason = "No validated category catalog has been loaded.";
            return false;
        }

        if (_timeProvider.GetUtcNow() - snapshot.LastSuccessfulRefreshAt > MaximumSnapshotAge)
        {
            unavailableReason = "The last validated category catalog is older than five minutes.";
            return false;
        }

        return true;
    }

    public CategoryCatalogReadiness GetReadiness()
    {
        if (!IsRichCatalogRequired)
        {
            return new CategoryCatalogReadiness(
                "ready",
                Required: false,
                Usable: true,
                CatalogVersion: null,
                LastSuccessfulRefreshAt: null,
                Detail: null);
        }

        var usable = TryCaptureUsable(out var snapshot, out var unavailableReason);
        var failure = Volatile.Read(ref _lastFailure);
        var degraded = !usable
            || failure is not null
                && (snapshot is null || failure.At > snapshot.LastSuccessfulRefreshAt);

        return new CategoryCatalogReadiness(
            degraded ? "degraded" : "ready",
            Required: true,
            Usable: usable,
            snapshot?.CatalogVersion,
            snapshot?.LastSuccessfulRefreshAt,
            unavailableReason ?? failure?.Detail);
    }

    private sealed record RefreshFailure(DateTimeOffset At, string Detail);
}
