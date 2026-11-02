using JiraTaskBar.Core.Storage;

namespace JiraTaskBar.Core.Tracking;

public sealed record ActiveSession(string Key, DateTimeOffset StartedUtc, DateTimeOffset LastSeenUtc);

public sealed record CompletedSession(string Key, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc)
{
    public TimeSpan Duration => EndedUtc - StartedUtc;
}

public sealed class TrackerState
{
    public ActiveSession? Active { get; set; }
}

public enum RecoveryChoice { KeepRunning, LogUntilLastSeen, Discard }

public sealed record ActiveTicketTransition(string? PreviousKey, string? CurrentKey, DateTimeOffset OccurredUtc);

/// <summary>
/// Tracks time against at most one issue. Every change is persisted so a running timer
/// survives a crash or restart (surfaced through <see cref="PendingRecovery"/>).
/// </summary>
public sealed class TimeTracker
{
    private readonly JsonFileStore<TrackerState> _store;
    private readonly TimeProvider _time;

    public TimeTracker(JsonFileStore<TrackerState> store, TimeProvider time)
    {
        _store = store;
        _time = time;
        PendingRecovery = store.Load()?.Active;
    }

    public ActiveSession? Active { get; private set; }

    /// <summary>A session that was running when the app last exited; resolve with <see cref="ResolveRecovery"/>.</summary>
    public ActiveSession? PendingRecovery { get; private set; }

    public string? ActiveKey => Active?.Key;

    public TimeSpan Elapsed => Active is null ? TimeSpan.Zero : Now - Active.StartedUtc;

    /// <summary>Raised whenever the active issue changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised with the previous and current ticket whenever the active ticket changes.</summary>
    public event EventHandler<ActiveTicketTransition>? Transitioned;

    /// <summary>Raised for each finished session; the subscriber posts the worklog.</summary>
    public event EventHandler<CompletedSession>? SessionCompleted;

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Clicking the active issue stops it; clicking another issue switches to it.</summary>
    public void Toggle(string key)
    {
        if (string.Equals(Active?.Key, key, StringComparison.OrdinalIgnoreCase))
            Stop();
        else
            Start(key);
    }

    public void Start(string key)
    {
        if (string.Equals(Active?.Key, key, StringComparison.OrdinalIgnoreCase)) return;
        var previous = Active?.Key;
        StopCore(null, raiseChanged: false);
        var now = Now;
        Active = new ActiveSession(key, now, now);
        Persist();
        Transitioned?.Invoke(this, new ActiveTicketTransition(previous, key, now));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public CompletedSession? Stop(DateTimeOffset? endUtc = null)
    {
        var previous = Active?.Key;
        var completed = StopCore(endUtc, raiseChanged: true);
        if (completed is not null)
            Transitioned?.Invoke(this, new ActiveTicketTransition(previous, null, completed.EndedUtc));
        return completed;
    }

    /// <summary>Records that the app is alive, so a crash can be logged up to the last-seen time.</summary>
    public void Heartbeat()
    {
        if (Active is null) return;
        Active = Active with { LastSeenUtc = Now };
        Persist();
    }

    /// <summary>Ends the current session at <paramref name="gapStartUtc"/> and restarts the same issue now.</summary>
    public void ExcludeGap(DateTimeOffset gapStartUtc)
    {
        if (Active is null) return;
        var key = Active.Key;
        StopCore(gapStartUtc, raiseChanged: false);
        var now = Now;
        Active = new ActiveSession(key, now, now);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResolveRecovery(RecoveryChoice choice)
    {
        var pending = PendingRecovery;
        PendingRecovery = null;
        if (pending is null) return;

        switch (choice)
        {
            case RecoveryChoice.KeepRunning:
                Active = pending with { LastSeenUtc = Now };
                Persist();
                Changed?.Invoke(this, EventArgs.Empty);
                break;
            case RecoveryChoice.LogUntilLastSeen:
                Persist();
                var end = pending.LastSeenUtc > pending.StartedUtc ? pending.LastSeenUtc : pending.StartedUtc;
                SessionCompleted?.Invoke(this, new CompletedSession(pending.Key, pending.StartedUtc, end));
                break;
            default:
                Persist();
                break;
        }
    }

    private CompletedSession? StopCore(DateTimeOffset? endUtc, bool raiseChanged)
    {
        if (Active is null) return null;
        var end = endUtc ?? Now;
        if (end < Active.StartedUtc) end = Active.StartedUtc;
        var completed = new CompletedSession(Active.Key, Active.StartedUtc, end);
        Active = null;
        Persist();
        SessionCompleted?.Invoke(this, completed);
        if (raiseChanged) Changed?.Invoke(this, EventArgs.Empty);
        return completed;
    }

    private void Persist() => _store.Save(new TrackerState { Active = Active });
}
