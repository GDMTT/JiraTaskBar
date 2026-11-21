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
    public void Active_sla_counts_down_when_the_projected_deadline_matches_its_remaining_time()
    {
        var observed = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var sla = new JiraSla("Response", false, false, false, 600_000, 300_000)
        {
            ObservedAt = observed,
            BreachAt = observed.AddMinutes(5),
        };
        var issue = Issue(null) with { Sla = sla };

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
}
