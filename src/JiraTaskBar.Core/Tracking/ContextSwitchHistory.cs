using JiraTaskBar.Core.Storage;

namespace JiraTaskBar.Core.Tracking;

public sealed record ContextInterval(string Key, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc)
{
    public TimeSpan Duration => EndedUtc - StartedUtc;
}

public sealed record ContextSwitch(string FromKey, string ToKey, DateTimeOffset OccurredUtc);

public sealed record ContextTicketSummary(string Key, int Intervals, TimeSpan Total, TimeSpan Average);

public sealed record ContextTransitionSummary(string FromKey, string ToKey, int Count);

public sealed record ContextSwitchReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int SwitchCount,
    int IntervalCount,
    TimeSpan TotalActive,
    TimeSpan AverageInterval,
    IReadOnlyList<ContextTicketSummary> Tickets,
    IReadOnlyList<ContextTransitionSummary> Transitions);

public static class LocalDateBoundary
{
    /// <summary>
    /// Returns the earliest UTC instant belonging to a date in <paramref name="timeZone"/>.
    /// Some zones skip or repeat midnight during a DST transition, so converting midnight
    /// directly can throw or select the later of two possible instants.
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime date, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local))
            local = local.AddMinutes(1);

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

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
    public const int CurrentVersion = 1;

    private readonly JsonFileStore<ContextSwitchHistoryState> _store;
    private ContextSwitchHistoryState _state;

    public ContextSwitchHistory(JsonFileStore<ContextSwitchHistoryState> store)
    {
        _store = store;
        _state = store.Load() ?? new ContextSwitchHistoryState();
    }

    public IReadOnlyList<ContextInterval> Intervals => _state.Intervals;
    public IReadOnlyList<ContextSwitch> Switches => _state.Switches;

    /// <summary>False when the file was written by a newer or otherwise unsupported schema.</summary>
    public bool IsWritable => _state.Version == CurrentVersion;

    public void Record(CompletedSession session)
    {
        EnsureWritable();
        var end = session.EndedUtc < session.StartedUtc ? session.StartedUtc : session.EndedUtc;
        _state.Intervals.Add(new ContextInterval(session.Key, session.StartedUtc, end));
        Save();
    }

    public void RecordSwitch(string? fromKey, string? toKey, DateTimeOffset occurredUtc)
    {
        if (string.IsNullOrWhiteSpace(fromKey) || string.IsNullOrWhiteSpace(toKey) ||
            fromKey.Equals(toKey, StringComparison.OrdinalIgnoreCase)) return;

        EnsureWritable();
        _state.Switches.Add(new ContextSwitch(fromKey, toKey, occurredUtc));
        Save();
    }

    public void Clear()
    {
        _state = new ContextSwitchHistoryState();
        Save();
    }

    /// <summary>Builds a report for the half-open UTC range [from, to), clipping intervals at its edges.</summary>
    public ContextSwitchReport BuildReport(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc < fromUtc) throw new ArgumentException("The report end must not precede its start.", nameof(toUtc));

        var intervals = _state.Intervals
            .Select(i => new ContextInterval(i.Key,
                i.StartedUtc < fromUtc ? fromUtc : i.StartedUtc,
                i.EndedUtc > toUtc ? toUtc : i.EndedUtc))
            .Where(i => i.EndedUtc > i.StartedUtc)
            .ToList();
        var switches = _state.Switches
            .Where(s => s.OccurredUtc >= fromUtc && s.OccurredUtc < toUtc)
            .ToList();

        var tickets = intervals
            .GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var total = TimeSpan.FromTicks(g.Sum(i => i.Duration.Ticks));
                return new ContextTicketSummary(g.Key, g.Count(), total,
                    TimeSpan.FromTicks(total.Ticks / g.Count()));
            })
            .OrderByDescending(t => t.Total)
            .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var transitions = switches
            .GroupBy(s => (s.FromKey.ToUpperInvariant(), s.ToKey.ToUpperInvariant()))
            .Select(g => new ContextTransitionSummary(g.First().FromKey, g.First().ToKey, g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.FromKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.ToKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var totalActive = TimeSpan.FromTicks(intervals.Sum(i => i.Duration.Ticks));

        return new ContextSwitchReport(fromUtc, toUtc, switches.Count, intervals.Count, totalActive,
            intervals.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(totalActive.Ticks / intervals.Count),
            tickets, transitions);
    }

    private void Save() => _store.Save(_state);

    private void EnsureWritable()
    {
        if (!IsWritable)
            throw new NotSupportedException(
                $"Context-switch history version {_state.Version} is not supported by this version of JiraTaskBar.");
    }
}
