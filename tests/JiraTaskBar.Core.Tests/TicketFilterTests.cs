using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.Core.Tests;

public class TicketFilterTests
{
    private static readonly JiraDurationSettings Durations = new(8, 5);
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static JiraIssue Issue(DateOnly? due = null) =>
        new("ABC-123", "Fix printer connection", null, "Open", "software", "Bug", DueDate: due);

    [Theory]
    [InlineData("1w 2d 3h 30m", 214200)]
    [InlineData("30m", 1800)]
    [InlineData("2H", 7200)]
    [InlineData("0m", 0)]
    public void Parses_and_formats_jira_durations(string text, long expected)
    {
        Assert.True(JiraDuration.TryParse(text, Durations, out var seconds));
        Assert.Equal(expected, seconds);
        Assert.True(JiraDuration.TryParse(JiraDuration.Format(seconds, Durations), Durations, out var roundTrip));
        Assert.Equal(seconds, roundTrip);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("1 day")]
    [InlineData("-1h")]
    [InlineData("1h rubbish")]
    public void Rejects_invalid_jira_durations(string text) =>
        Assert.False(JiraDuration.TryParse(text, Durations, out _));

    [Fact]
    public void Uses_site_specific_day_and_week_lengths()
    {
        var settings = new JiraDurationSettings(7.5m, 4);
        Assert.True(JiraDuration.TryParse("1w 1d", settings, out var seconds));
        Assert.Equal(135000, seconds);
    }

    [Fact]
    public void Unchanged_lossy_display_preserves_exact_bound()
    {
        var displayed = JiraDuration.Format(90, Durations);
        Assert.Equal("1m", displayed);

        Assert.True(JiraDuration.TryParsePreservingOriginal(displayed, displayed, 90, Durations, out var unchanged));
        Assert.Equal(90, unchanged);
        Assert.True(JiraDuration.TryParsePreservingOriginal("2m", displayed, 90, Durations, out var edited));
        Assert.Equal(120, edited);
    }

    [Fact]
    public void Combines_types_key_words_and_due_bounds_case_insensitively()
    {
        var filter = new TicketFilter
        {
            Types = ["bug", "Task"],
            Key = "c-12",
            Summary = "PRINTER fix",
            MinimumDueSeconds = 60,
            MaximumDueSeconds = 13 * 60 * 60,
            DueDate = DueDateFilter.HasDueDate,
        };
        Assert.True(filter.Matches(Issue(new DateOnly(2026, 11, 9)), Now, TimeZoneInfo.Utc));
        Assert.False(filter.Matches(Issue(new DateOnly(2026, 11, 10)), Now, TimeZoneInfo.Utc));
        Assert.False(filter.Matches(Issue(), Now, TimeZoneInfo.Utc));
        Assert.False(filter.Matches(Issue(new DateOnly(2026, 11, 9)) with { Summary = "Fix scanner" }, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Distinguishes_missing_due_dates_and_overdue_tickets()
    {
        var overdue = Issue(new DateOnly(2026, 11, 8));
        var upcoming = Issue(new DateOnly(2026, 11, 10));
        Assert.True(new TicketFilter { DueDate = DueDateFilter.NoDueDate }.Matches(Issue(), Now, TimeZoneInfo.Utc));
        Assert.False(new TicketFilter { DueDate = DueDateFilter.NoDueDate }.Matches(upcoming, Now, TimeZoneInfo.Utc));
        Assert.True(new TicketFilter { DueDate = DueDateFilter.Overdue }.Matches(overdue, Now, TimeZoneInfo.Utc));
        Assert.False(new TicketFilter { DueDate = DueDateFilter.Overdue }.Matches(upcoming, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Deadline_bounds_are_inclusive_and_change_as_time_passes()
    {
        var issue = Issue(new DateOnly(2026, 11, 9));
        var filter = new TicketFilter { MinimumDueSeconds = 12 * 60 * 60, MaximumDueSeconds = 12 * 60 * 60 };
        Assert.True(filter.Matches(issue, Now, TimeZoneInfo.Utc));
        Assert.False(filter.Matches(issue, Now.AddSeconds(1), TimeZoneInfo.Utc));
        Assert.True(filter.UsesCurrentTime);
    }

    [Fact]
    public void Deadline_bounds_compare_fractional_seconds_without_rounding()
    {
        var issue = Issue(new DateOnly(2026, 11, 9));
        var deadline = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var maximum = new TicketFilter { MaximumDueSeconds = 60 };
        var minimum = new TicketFilter { MinimumDueSeconds = 60 };

        Assert.False(maximum.Matches(issue, deadline.AddSeconds(-60.5), TimeZoneInfo.Utc));
        Assert.False(minimum.Matches(issue, deadline.AddSeconds(-59.5), TimeZoneInfo.Utc));
        Assert.True(maximum.Matches(issue, deadline.AddSeconds(-60), TimeZoneInfo.Utc));
        Assert.True(minimum.Matches(issue, deadline.AddSeconds(-60), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Reset_matches_everything()
    {
        Assert.True(new TicketFilter().Matches(Issue(), Now, TimeZoneInfo.Utc));
        Assert.False(new TicketFilter().IsActive);
    }
}
