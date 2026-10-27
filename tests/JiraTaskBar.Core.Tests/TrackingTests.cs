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
