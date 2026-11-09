using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.Core.Jira;

public enum DueDateKind { None, Upcoming, Soon, Today, Overdue }
public sealed record DueDateState(DueDateKind Kind, double? RemainingFraction, string Tooltip);

public static class DueDateProgress
{
    public static DueDateState Calculate(JiraIssue issue, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (issue.DueDate is not { } due || due == DateOnly.MaxValue)
            return new(DueDateKind.None, null, "No due date");
        var deadline = WorklogCalendarLayout.LocalDateTime(due.AddDays(1), TimeOnly.MinValue, zone);
        var remaining = deadline - now;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var kind = remaining <= TimeSpan.Zero ? DueDateKind.Overdue : due == today ? DueDateKind.Today
            : remaining <= TimeSpan.FromDays(2) ? DueDateKind.Soon : DueDateKind.Upcoming;
        double? fraction = issue.CreatedAt is { } created && created < deadline
            ? Math.Clamp(remaining.TotalSeconds / (deadline - created).TotalSeconds, 0, 1) : null;
        var duration = remaining.Duration();
        var label = kind switch { DueDateKind.Today => "Due today", DueDateKind.Soon => "Due soon",
            DueDateKind.Overdue => "Overdue", _ => "Upcoming" };
        var countdown = $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
        return new(kind, fraction, $"{label}: {due:yyyy-MM-dd} (end of day, {zone.DisplayName})\n" +
            (kind == DueDateKind.Overdue ? $"Overdue by {countdown}" : $"Remaining: {countdown}") +
            (fraction.HasValue ? "\nPie: remaining share of creation-to-deadline interval" : "\nPercentage unavailable: no valid creation baseline"));
    }
}
