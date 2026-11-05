using JiraTaskBar.Core.Storage;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.Core.Tests;

public class TimeTrackerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 10, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly List<CompletedSession> _completed = [];

    public void Dispose() => _dir.Dispose();

    private JsonFileStore<TrackerState> Store => new(_dir.File("state.json"));

    private TimeTracker NewTracker()
    {
        var tracker = new TimeTracker(Store, _time);
        tracker.SessionCompleted += (_, s) => _completed.Add(s);
        return tracker;
    }

    [Fact]
    public void Toggle_starts_and_stops_the_same_issue()
    {
        var tracker = NewTracker();

        tracker.Toggle("ABC-1");
        Assert.Equal("ABC-1", tracker.ActiveKey);
        _time.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(TimeSpan.FromMinutes(25), tracker.Elapsed);

        tracker.Toggle("abc-1");
        Assert.Null(tracker.ActiveKey);
        var session = Assert.Single(_completed);
        Assert.Equal("ABC-1", session.Key);
        Assert.Equal(TimeSpan.FromMinutes(25), session.Duration);
    }

    [Fact]
    public void Selecting_another_issue_completes_the_first()
    {
        var tracker = NewTracker();
        tracker.Toggle("ABC-1");
        _time.Advance(TimeSpan.FromMinutes(10));

        tracker.Toggle("ABC-2");

        Assert.Equal("ABC-2", tracker.ActiveKey);
        var first = Assert.Single(_completed);
        Assert.Equal("ABC-1", first.Key);
        Assert.Equal(TimeSpan.FromMinutes(10), first.Duration);
    }

    [Fact]
    public void Selecting_another_issue_reports_one_direct_transition()
    {
        var tracker = NewTracker();
        var transitions = new List<ActiveTicketTransition>();
        tracker.Transitioned += (_, transition) => transitions.Add(transition);

        tracker.Start("ABC-1");
        _time.Advance(TimeSpan.FromMinutes(10));
        tracker.Start("ABC-2");

        Assert.Collection(transitions,
            first =>
            {
                Assert.Null(first.PreviousKey);
                Assert.Equal("ABC-1", first.CurrentKey);
            },
            second =>
            {
                Assert.Equal("ABC-1", second.PreviousKey);
                Assert.Equal("ABC-2", second.CurrentKey);
            });
    }

    [Fact]
    public void Running_session_is_offered_for_recovery_after_restart()
    {
        var tracker = NewTracker();
        tracker.Start("ABC-1");
        _time.Advance(TimeSpan.FromMinutes(30));
        tracker.Heartbeat();
        _time.Advance(TimeSpan.FromHours(2)); // app was dead for two hours

        var restarted = NewTracker();

        Assert.Null(restarted.ActiveKey);
        Assert.Equal("ABC-1", restarted.PendingRecovery?.Key);

        restarted.ResolveRecovery(RecoveryChoice.LogUntilLastSeen);
        var logged = Assert.Single(_completed);
        Assert.Equal(TimeSpan.FromMinutes(30), logged.Duration);
        Assert.Null(restarted.PendingRecovery);
        Assert.Null(new TimeTracker(Store, _time).PendingRecovery);
    }

    [Fact]
    public void Recovery_keep_running_continues_from_original_start()
    {
        NewTracker().Start("ABC-1");
        _time.Advance(TimeSpan.FromMinutes(45));

        var restarted = NewTracker();
        restarted.ResolveRecovery(RecoveryChoice.KeepRunning);

        Assert.Equal("ABC-1", restarted.ActiveKey);
        Assert.Equal(TimeSpan.FromMinutes(45), restarted.Elapsed);
        Assert.Empty(_completed);
    }

    [Fact]
    public void Recovery_discard_logs_nothing()
    {
        NewTracker().Start("ABC-1");
        var restarted = NewTracker();

        restarted.ResolveRecovery(RecoveryChoice.Discard);

        Assert.Null(restarted.ActiveKey);
        Assert.Empty(_completed);
        Assert.Null(new TimeTracker(Store, _time).PendingRecovery);
    }

    [Fact]
    public void Exclude_gap_splits_the_session()
    {
        var tracker = NewTracker();
        tracker.Start("ABC-1");
        _time.Advance(TimeSpan.FromMinutes(20));
        var lockedAt = _time.Now;
        _time.Advance(TimeSpan.FromMinutes(60));

        tracker.ExcludeGap(lockedAt);

        var first = Assert.Single(_completed);
        Assert.Equal(TimeSpan.FromMinutes(20), first.Duration);
        Assert.Equal("ABC-1", tracker.ActiveKey);
        Assert.Equal(TimeSpan.Zero, tracker.Elapsed);
    }
}

public class ContextSwitchHistoryTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Persists_intervals_and_only_direct_ticket_switches()
    {
        var store = new JsonFileStore<ContextSwitchHistoryState>(_dir.File("context.json"));
        var history = new ContextSwitchHistory(store);
        var start = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);

        history.Record(new CompletedSession("ABC-1", start, start.AddMinutes(12)));
        history.RecordSwitch(null, "ABC-1", start);
        history.RecordSwitch("ABC-1", "ABC-2", start.AddMinutes(12));
        history.RecordSwitch("ABC-2", null, start.AddMinutes(20));

        var reloaded = new ContextSwitchHistory(store);
        Assert.Equal(TimeSpan.FromMinutes(12), Assert.Single(reloaded.Intervals).Duration);
        var contextSwitch = Assert.Single(reloaded.Switches);
        Assert.Equal("ABC-1", contextSwitch.FromKey);
        Assert.Equal("ABC-2", contextSwitch.ToKey);
    }

    [Fact]
    public void Clear_removes_local_history()
    {
        var store = new JsonFileStore<ContextSwitchHistoryState>(_dir.File("context.json"));
        var history = new ContextSwitchHistory(store);
        var now = DateTimeOffset.UtcNow;
        history.Record(new CompletedSession("ABC-1", now, now.AddMinutes(1)));
        history.Clear();

        var reloaded = new ContextSwitchHistory(store);
        Assert.Empty(reloaded.Intervals);
        Assert.Empty(reloaded.Switches);
    }

    [Fact]
    public void Unsupported_version_is_preserved_and_refuses_implicit_writes()
    {
        var path = _dir.File("context.json");
        var store = new JsonFileStore<ContextSwitchHistoryState>(path);
        store.Save(new ContextSwitchHistoryState
        {
            Version = ContextSwitchHistory.CurrentVersion + 1,
            Intervals = [new ContextInterval("ABC-9", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
        });
        var original = File.ReadAllText(path);
        var history = new ContextSwitchHistory(store);

        Assert.False(history.IsWritable);
        Assert.Throws<NotSupportedException>(() =>
            history.Record(new CompletedSession("ABC-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal("ABC-9", Assert.Single(history.Intervals).Key);
    }

    [Fact]
    public void Report_clips_intervals_and_groups_tickets_and_transitions()
    {
        var store = new JsonFileStore<ContextSwitchHistoryState>(_dir.File("context.json"));
        var history = new ContextSwitchHistory(store);
        var day = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        history.Record(new CompletedSession("ABC-1", day.AddMinutes(-10), day.AddMinutes(20)));
        history.Record(new CompletedSession("abc-1", day.AddHours(1), day.AddHours(1.5)));
        history.Record(new CompletedSession("ABC-2", day.AddHours(2), day.AddHours(2.25)));
        history.RecordSwitch("ABC-1", "ABC-2", day.AddHours(2));
        history.RecordSwitch("abc-1", "abc-2", day.AddHours(2.5));

        var report = history.BuildReport(day, day.AddHours(3));

        Assert.Equal(2, report.SwitchCount);
        Assert.Equal(3, report.IntervalCount);
        Assert.Equal(TimeSpan.FromMinutes(65), report.TotalActive);
        var abc1 = Assert.Single(report.Tickets, t => t.Key.Equals("ABC-1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, abc1.Intervals);
        Assert.Equal(TimeSpan.FromMinutes(50), abc1.Total);
        var transition = Assert.Single(report.Transitions, t => t.FromKey.Equals("ABC-1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, transition.Count);
    }

    [Fact]
    public void Local_date_boundary_handles_a_midnight_dst_gap()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 0, 0, 0), 3, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 0, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), start, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/MidnightGap", TimeSpan.Zero, "Midnight gap", "Standard", "Daylight", [rule]);

        var boundary = LocalDateBoundary.ToUtc(new DateTime(2026, 3, 1), zone);
        var local = TimeZoneInfo.ConvertTime(boundary, zone);

        Assert.Equal(new DateTime(2026, 3, 1), local.Date);
        Assert.Equal(1, local.Hour);
    }
}

public class WorklogCalendarLayoutTests
{
    [Fact]
    public void Ranges_follow_the_selected_view_and_first_day_of_week()
    {
        var anchor = new DateOnly(2026, 11, 5);

        var day = WorklogCalendarLayout.RangeFor(WorklogCalendarView.Day, anchor, DayOfWeek.Monday);
        var week = WorklogCalendarLayout.RangeFor(WorklogCalendarView.Week, anchor, DayOfWeek.Monday);
        var month = WorklogCalendarLayout.RangeFor(WorklogCalendarView.Month, anchor, DayOfWeek.Monday);

        Assert.Equal(new DateOnly(2026, 11, 5), day.Start);
        Assert.Equal(1, day.DayCount);
        Assert.Equal(new DateOnly(2026, 11, 2), week.Start);
        Assert.Equal(7, week.DayCount);
        Assert.Equal(new DateOnly(2026, 11, 1), month.Start);
        Assert.Equal(new DateOnly(2026, 12, 1), month.EndExclusive);
    }

    [Theory]
    [InlineData(5, 61, 1)]
    [InlineData(15, 901, 2)]
    [InlineData(30, 3600, 2)]
    [InlineData(17, 3600, 2)] // unsupported values normalize to 30 minutes
    public void Worklog_spans_round_up_to_whole_resolution_slots(int resolution, int seconds, int expected)
    {
        Assert.Equal(expected, WorklogCalendarLayout.SlotSpan(seconds, resolution));
    }

    [Fact]
    public void Navigation_moves_by_the_active_view()
    {
        var anchor = new DateOnly(2026, 11, 5);

        Assert.Equal(new DateOnly(2026, 11, 6),
            WorklogCalendarLayout.Move(anchor, WorklogCalendarView.Day, 1));
        Assert.Equal(new DateOnly(2026, 10, 29),
            WorklogCalendarLayout.Move(anchor, WorklogCalendarView.Week, -1));
        Assert.Equal(new DateOnly(2026, 12, 5),
            WorklogCalendarLayout.Move(anchor, WorklogCalendarView.Month, 1));
    }

    [Fact]
    public void Ambiguous_local_time_uses_the_earliest_instant()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), start, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Ambiguous", TimeSpan.Zero, "Test", "Standard", "Daylight", [rule]);

        var instant = WorklogCalendarLayout.LocalDateTime(
            new DateOnly(2026, 11, 1), new TimeOnly(1, 30), zone);

        Assert.Equal(TimeSpan.FromHours(1), instant.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.Zero), instant.ToUniversalTime());
    }

    [Fact]
    public void Spring_forward_worklogs_and_selections_use_their_correct_time_basis()
    {
        var zone = DstZone();
        var start = WorklogCalendarLayout.LocalDateTime(
            new DateOnly(2026, 3, 1), new TimeOnly(1, 30), zone);

        Assert.Equal(6, WorklogCalendarLayout.DisplaySlotSpan(start, 2 * 60 * 60, 30, zone));
        Assert.Equal(60 * 60, WorklogCalendarLayout.SelectionSeconds(
            new DateOnly(2026, 3, 1), 3, 4, 30, zone));
    }

    [Fact]
    public void Repeated_autumn_hour_does_not_collapse_calendar_duration()
    {
        var zone = DstZone();
        var start = WorklogCalendarLayout.LocalDateTime(
            new DateOnly(2026, 11, 1), new TimeOnly(1, 30), zone);

        Assert.Equal(4, WorklogCalendarLayout.DisplaySlotSpan(start, 2 * 60 * 60, 30, zone));
        Assert.Equal(2 * 60 * 60, WorklogCalendarLayout.SelectionSeconds(
            new DateOnly(2026, 11, 1), 3, 2, 30, zone));
    }

    [Fact]
    public void Overlapping_entries_are_assigned_to_separate_lanes()
    {
        var lanes = WorklogCalendarLayout.AssignOverlapLanes([(18, 20), (18, 19), (20, 21)]);

        Assert.NotEqual(lanes[0].Lane, lanes[1].Lane);
        Assert.Equal(2, lanes[0].LaneCount);
        Assert.Equal(2, lanes[2].LaneCount);
    }

    private static TimeZoneInfo DstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Test/CalendarDst", TimeSpan.Zero, "Test", "Standard", "Daylight", [rule]);
    }
}

public class WorklogTests : IDisposable
{
    private readonly TempDir _dir = new();
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(0, ShortSessionMode.RoundUpToMinute, null)]
    [InlineData(20, ShortSessionMode.RoundUpToMinute, 60)]
    [InlineData(20, ShortSessionMode.Skip, null)]
    [InlineData(60, ShortSessionMode.Skip, 60)]
    [InlineData(3725, ShortSessionMode.Skip, 3725)]
    public void Policy_handles_short_sessions(int seconds, ShortSessionMode mode, int? expected)
    {
        Assert.Equal(expected, WorklogPolicy.SecondsToLog(TimeSpan.FromSeconds(seconds), mode));
    }

    [Fact]
    public async Task Failed_post_is_queued_persisted_and_retried()
    {
        var jira = new FakeJiraClient { FailuresRemaining = 1 };
        var store = new JsonFileStore<List<PendingWorklog>>(_dir.File("queue.json"));
        var service = new WorklogService(() => jira, new WorklogQueue(store), () => ShortSessionMode.Skip);

        var result = await service.LogAsync(new CompletedSession("ABC-1", Start, Start.AddMinutes(15)));

        Assert.Equal(WorklogOutcome.Queued, result.Outcome);
        Assert.Equal(1, service.PendingCount);

        // A new service (as after restart) sees the persisted queue and retries it.
        var reloaded = new WorklogService(() => jira, new WorklogQueue(store), () => ShortSessionMode.Skip);
        Assert.Equal(1, reloaded.PendingCount);
        Assert.Equal(0, await reloaded.RetryPendingAsync());

        var posted = Assert.Single(jira.Worklogs);
        Assert.Equal("ABC-1", posted.Key);
        Assert.Equal(900, posted.Seconds);
        Assert.Equal(Start, posted.Started.ToUniversalTime());
    }

    [Fact]
    public async Task Short_session_is_skipped()
    {
        var jira = new FakeJiraClient();
        var service = new WorklogService(() => jira,
            new WorklogQueue(new JsonFileStore<List<PendingWorklog>>(_dir.File("q.json"))), () => ShortSessionMode.Skip);

        var result = await service.LogAsync(new CompletedSession("ABC-1", Start, Start.AddSeconds(30)));

        Assert.Equal(WorklogOutcome.Skipped, result.Outcome);
        Assert.Empty(jira.Worklogs);
    }
}
