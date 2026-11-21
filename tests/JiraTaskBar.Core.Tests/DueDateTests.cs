using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.Core.Tests;

public class DueDateTests
{
    private static JiraIssue Issue(DateOnly? due) => new("A-1", "Due", null, "Open", "software", "Task",
        CreatedAt: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), DueDate: due);
    [Fact]
    public void Missing_and_invalid_baselines_are_neutral()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(DueDateKind.None, DueDateProgress.Calculate(Issue(null), now, TimeZoneInfo.Utc).Kind);
        Assert.Null(DueDateProgress.Calculate(Issue(new DateOnly(2026, 11, 10)) with { CreatedAt = null }, now, TimeZoneInfo.Utc).RemainingFraction);
        foreach (var created in new[] { new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero) })
        {
            var invalid = DueDateProgress.Calculate(Issue(new DateOnly(2026, 11, 10)) with { CreatedAt = created }, now, TimeZoneInfo.Utc);
            Assert.Null(invalid.RemainingFraction);
            Assert.Equal(DueDateKind.Soon, invalid.Kind);
            Assert.Contains("1d 12h 0m 0s", invalid.Tooltip);
        }
    }
    [Fact]
    public void Local_end_of_day_and_exact_deadline_are_respected()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/+13", TimeSpan.FromHours(13), "Test", "Test");
        var issue = Issue(new DateOnly(2026, 11, 9));
        var noon = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(13));
        var state = DueDateProgress.Calculate(issue, noon, zone);
        Assert.Equal(DueDateKind.Today, state.Kind);
        Assert.Contains("12h 0m 0s", state.Tooltip);
        Assert.Equal(DueDateKind.Overdue, DueDateProgress.Calculate(issue, noon.AddHours(12), zone).Kind);
        Assert.Equal(0d, DueDateProgress.Calculate(issue, noon.AddDays(1), zone).RemainingFraction);
        Assert.Equal(DueDateKind.Soon, DueDateProgress.Calculate(issue with { DueDate = new DateOnly(2026, 11, 10) }, noon, zone).Kind);
        Assert.Equal(DueDateKind.Upcoming, DueDateProgress.Calculate(issue with { DueDate = new DateOnly(2026, 11, 20) }, noon, zone).Kind);
    }
    [Fact]
    public void Search_reads_dates_and_refresh_recalculates_the_percentage()
    {
        var parsed = JiraPayloads.ParseSearch(System.Text.Json.Nodes.JsonNode.Parse("""
            {"issues":[{"key":"A-1","fields":{"created":"2026-11-01T00:00:00.000+0000","duedate":"2026-11-10"}}]}
            """), new Dictionary<string, JiraPriority>()).Issues.Single();
        Assert.NotNull(parsed.CreatedAt);
        Assert.Equal(new DateOnly(2026, 11, 10), parsed.DueDate);
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(0.15, DueDateProgress.Calculate(parsed, now, TimeZoneInfo.Utc).RemainingFraction!.Value, 10);
        Assert.Equal(2.5 / 11, DueDateProgress.Calculate(parsed with { DueDate = new DateOnly(2026, 11, 11) }, now, TimeZoneInfo.Utc).RemainingFraction!.Value, 10);
        Assert.True(DueDateProgress.Calculate(parsed, now, TimeZoneInfo.Utc).RemainingFraction >
            DueDateProgress.Calculate(parsed, now.AddHours(1), TimeZoneInfo.Utc).RemainingFraction);
    }

    [Fact]
    public void Active_resolution_pie_uses_the_creation_to_deadline_interval()
    {
        var created = new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
        var issue = Issue(null) with
        {
            CreatedAt = created,
            Sla = new JiraSla("Time to resolution", false, false, false, 600_000, 300_000)
            {
                ObservedAt = created.AddHours(2),
                BreachAt = created.AddHours(4),
            },
        };
        var observed = issue.Sla.ObservedAt;

        Assert.Equal(0.5, DueDateProgress.CalculateUrgency(issue, observed, TimeZoneInfo.Utc).RemainingFraction);
        var state = DueDateProgress.CalculateUrgency(issue, observed.AddMinutes(5), TimeZoneInfo.Utc);
        Assert.Equal(115d / 240, state.RemainingFraction);
        Assert.Contains("of the creation-to-deadline interval remains", state.Tooltip);
    }

    [Fact]
    public void Paused_resolution_pie_keeps_jira_remaining_time_across_refreshes()
    {
        var created = new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
        var observed = created.AddHours(2);
        var sla = new JiraSla("Time to resolution", false, false, true, 600_000, 300_000)
        {
            ObservedAt = observed,
            BreachAt = created.AddHours(4),
        };
        var issue = Issue(null) with { CreatedAt = created, Sla = sla };
        var initial = DueDateProgress.CalculateUrgency(issue, observed, TimeZoneInfo.Utc);

        var nextObservation = observed.AddHours(1);
        var refreshed = issue with { Sla = sla with { ObservedAt = nextObservation } };
        var afterRefresh = DueDateProgress.CalculateUrgency(refreshed, nextObservation, TimeZoneInfo.Utc);
        Assert.Equal(0.5, initial.RemainingFraction);
        Assert.Equal(initial.RemainingFraction, afterRefresh.RemainingFraction);
        Assert.Contains("Paused", afterRefresh.Tooltip);
        Assert.Contains("0d 0h 5m 0s", afterRefresh.Tooltip);
    }

    [Fact]
    public void Active_sla_counts_down_when_the_projected_deadline_matches_its_remaining_time()
    {
        var observed = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var sla = new JiraSla("Response", false, false, false, 600_000, 300_000)
        {
            ObservedAt = observed,
            BreachAt = observed.AddMinutes(5),
        };
        var issue = Issue(null) with { CreatedAt = null, Sla = sla };

        var state = DueDateProgress.CalculateUrgency(issue, observed.AddMinutes(2), TimeZoneInfo.Utc);
        Assert.Equal(0.3, state.RemainingFraction);
        Assert.Contains("Estimated remaining", state.Tooltip);
        var estimatedDeadline = DueDateProgress.CalculateUrgency(issue, observed.AddMinutes(5), TimeZoneInfo.Utc);
        Assert.Equal(0d, estimatedDeadline.RemainingFraction);
        Assert.Equal(DueDateKind.Upcoming, estimatedDeadline.Kind);
        var confirmed = DueDateProgress.CalculateUrgency(issue with { Sla = sla with { Breached = true } },
            observed.AddMinutes(5), TimeZoneInfo.Utc);
        Assert.Equal(DueDateKind.Overdue, confirmed.Kind);
    }

    [Theory]
    [InlineData(true, 5)]
    [InlineData(false, 65)]
    public void Paused_or_calendar_limited_sla_retains_the_jira_snapshot(bool paused, int minutesUntilBreach)
    {
        var observed = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var issue = Issue(null) with
        {
            CreatedAt = null,
            Sla = new JiraSla("Response", false, false, paused, 600_000, 300_000)
            {
                ObservedAt = observed,
                BreachAt = observed.AddMinutes(minutesUntilBreach),
            },
        };

        var state = DueDateProgress.CalculateUrgency(issue, observed.AddMinutes(2), TimeZoneInfo.Utc);
        Assert.Equal(0.5, state.RemainingFraction);
        Assert.Contains("Remaining when checked at", state.Tooltip);
    }

    [Theory]
    [InlineData(2, 180_000)]
    [InlineData(5, 0)]
    [InlineData(7, -120_000)]
    public void Sla_sort_time_crosses_zero_at_a_continuous_deadline(int minutesElapsed, double expected)
    {
        var observed = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var sla = new JiraSla("Time to Resolution", false, false, false, 600_000, 300_000)
        {
            ObservedAt = observed,
            BreachAt = observed.AddMinutes(5),
        };

        Assert.Equal(expected, sla.RemainingForSort(observed.AddMinutes(minutesElapsed)));
    }

    [Theory]
    [InlineData(true, 5, false, 300_000L, 300_000d)]
    [InlineData(false, 65, false, 300_000L, 300_000d)]
    [InlineData(false, 5, true, 300_000L, null)]
    [InlineData(false, 5, false, null, null)]
    public void Sla_sort_time_uses_only_available_active_sla_time(bool paused, int minutesUntilBreach,
        bool completed, long? snapshot, double? expected)
    {
        var observed = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var sla = new JiraSla("Time to Resolution", completed, false, paused, 600_000, snapshot)
        {
            ObservedAt = observed,
            BreachAt = observed.AddMinutes(minutesUntilBreach),
        };

        Assert.Equal(expected, sla.RemainingForSort(observed.AddMinutes(7)));
    }
}
