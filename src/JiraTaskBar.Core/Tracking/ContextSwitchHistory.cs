using JiraTaskBar.Core.Storage;

namespace JiraTaskBar.Core.Tracking;

public sealed record ContextInterval(string Key, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc)
{
    public TimeSpan Duration => EndedUtc - StartedUtc;
}

public sealed record ContextSwitch(string FromKey, string ToKey, DateTimeOffset OccurredUtc);

public sealed class ContextSwitchHistoryState
{
    public int Version { get; set; } = 1;
    public List<ContextInterval> Intervals { get; set; } = [];
    public List<ContextSwitch> Switches { get; set; } = [];
}

/// <summary>
/// Local-only history used to measure uninterrupted work intervals and direct ticket-to-ticket
/// switches. This data is deliberately separate from Jira worklog retry state.
/// </summary>
public sealed class ContextSwitchHistory
{
    private readonly JsonFileStore<ContextSwitchHistoryState> _store;
    private ContextSwitchHistoryState _state;

    public ContextSwitchHistory(JsonFileStore<ContextSwitchHistoryState> store)
    {
        _store = store;
        _state = store.Load() is { Version: 1 } loaded ? loaded : new ContextSwitchHistoryState();
    }

    public IReadOnlyList<ContextInterval> Intervals => _state.Intervals;
    public IReadOnlyList<ContextSwitch> Switches => _state.Switches;

    public void Record(CompletedSession session)
    {
        var end = session.EndedUtc < session.StartedUtc ? session.StartedUtc : session.EndedUtc;
        _state.Intervals.Add(new ContextInterval(session.Key, session.StartedUtc, end));
        Save();
    }

    public void RecordSwitch(string? fromKey, string? toKey, DateTimeOffset occurredUtc)
    {
        if (string.IsNullOrWhiteSpace(fromKey) || string.IsNullOrWhiteSpace(toKey) ||
            fromKey.Equals(toKey, StringComparison.OrdinalIgnoreCase)) return;

        _state.Switches.Add(new ContextSwitch(fromKey, toKey, occurredUtc));
        Save();
    }

    public void Clear()
    {
        _state = new ContextSwitchHistoryState();
        Save();
    }

    private void Save() => _store.Save(_state);
}
