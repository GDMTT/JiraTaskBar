using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Storage;

namespace JiraTaskBar.Core.Tracking;

public static class WorklogPolicy
{
    /// <summary>Seconds to send to Jira, or null when the session should not be logged.</summary>
    public static int? SecondsToLog(TimeSpan duration, ShortSessionMode mode)
    {
        var seconds = (int)Math.Round(duration.TotalSeconds);
        if (seconds >= 60) return seconds;
        return mode == ShortSessionMode.RoundUpToMinute && seconds > 0 ? 60 : null;
    }
}

public sealed record PendingWorklog(Guid Id, string Key, DateTimeOffset StartedUtc, int Seconds, string? LastError, int Attempts);

/// <summary>Worklogs that failed to post, persisted so they are retried after a restart.</summary>
public sealed class WorklogQueue
{
    private readonly JsonFileStore<List<PendingWorklog>> _store;
    private readonly List<PendingWorklog> _items;
    private readonly object _gate = new();

    public WorklogQueue(JsonFileStore<List<PendingWorklog>> store)
    {
        _store = store;
        _items = store.Load() ?? [];
    }

    public int Count { get { lock (_gate) return _items.Count; } }

    public IReadOnlyList<PendingWorklog> Snapshot() { lock (_gate) return _items.ToList(); }

    public void Enqueue(PendingWorklog item) { lock (_gate) { _items.Add(item); Save(); } }

    public void Replace(PendingWorklog item)
    {
        lock (_gate)
        {
            var i = _items.FindIndex(x => x.Id == item.Id);
            if (i >= 0) { _items[i] = item; Save(); }
        }
    }

    public void Remove(Guid id) { lock (_gate) { _items.RemoveAll(x => x.Id == id); Save(); } }

    private void Save() => _store.Save(_items);
}

public enum WorklogOutcome { Posted, Skipped, Queued }

/// <param name="IsAuthFailure">Jira rejected the credentials (401); the worklog was queued.</param>
public sealed record WorklogResult(WorklogOutcome Outcome, int Seconds, string? Error = null, bool IsAuthFailure = false);

/// <summary>Posts finished sessions as Jira worklogs, queueing failures for retry.</summary>
public sealed class WorklogService
{
    private readonly Func<IJiraClient?> _client;
    private readonly WorklogQueue _queue;
    private readonly Func<ShortSessionMode> _mode;
    private readonly SemaphoreSlim _retryGate = new(1, 1);

    public WorklogService(Func<IJiraClient?> client, WorklogQueue queue, Func<ShortSessionMode> mode)
    {
        _client = client;
        _queue = queue;
        _mode = mode;
    }

    public int PendingCount => _queue.Count;

    public IReadOnlyList<PendingWorklog> Pending => _queue.Snapshot();

    public event EventHandler? QueueChanged;

    public async Task<WorklogResult> LogAsync(CompletedSession session, CancellationToken ct = default)
    {
        if (WorklogPolicy.SecondsToLog(session.Duration, _mode()) is not { } seconds)
            return new WorklogResult(WorklogOutcome.Skipped, 0);

        try
        {
            var client = _client() ?? throw new InvalidOperationException("Not connected to Jira.");
            await client.AddWorklogAsync(session.Key, session.StartedUtc.ToLocalTime(), seconds, null, ct);
            return new WorklogResult(WorklogOutcome.Posted, seconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _queue.Enqueue(new PendingWorklog(Guid.NewGuid(), session.Key, session.StartedUtc, seconds, ex.Message, 1));
            QueueChanged?.Invoke(this, EventArgs.Empty);
            return new WorklogResult(WorklogOutcome.Queued, seconds, ex.Message, ex is JiraApiException { StatusCode: 401 });
        }
    }

    /// <returns>The number of worklogs still pending.</returns>
    public async Task<int> RetryPendingAsync(CancellationToken ct = default)
    {
        if (_queue.Count == 0) return 0;
        await _retryGate.WaitAsync(ct);
        try
        {
            var client = _client();
            if (client is null) return _queue.Count;
            foreach (var item in _queue.Snapshot())
            {
                try
                {
                    await client.AddWorklogAsync(item.Key, item.StartedUtc.ToLocalTime(), item.Seconds, null, ct);
                    _queue.Remove(item.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _queue.Replace(item with { LastError = ex.Message, Attempts = item.Attempts + 1 });
                }
            }
        }
        finally
        {
            _retryGate.Release();
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
        return _queue.Count;
    }

    public void Discard(Guid id)
    {
        _queue.Remove(id);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }
}
