using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.Core.Jira;

public enum DueDateKind { None, Upcoming, Soon, Today, Overdue, Met }
public sealed record DueDateState(DueDateKind Kind, double? RemainingFraction, string Tooltip);

public static class DueDateProgress
{
    public static DueDateState CalculateUrgency(JiraIssue issue, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (issue.Sla is not { } sla) return Calculate(issue, now, zone);

        var kind = sla.Breached ? DueDateKind.Overdue : sla.Completed ? DueDateKind.Met : DueDateKind.Upcoming;
        var status = kind switch
        {
            DueDateKind.Overdue => "Breached",
            DueDateKind.Met => "Met",
            _ => sla.Paused ? "Paused" : "In progress",
        };
        var tooltip = $"{sla.Name} (SLA): {status}";
        double? fraction = null;
        if (!sla.Completed && !sla.Breached && now - sla.ObservedAt >= TimeSpan.FromMinutes(10))
            return new(kind, null, $"{sla.Name} (SLA): Waiting for a fresh Jira reading");
        if (!sla.Completed && sla.RemainingMilliseconds is { } remaining)
        {
            if (sla.GoalMilliseconds is > 0 and var goal)
                fraction = Math.Clamp((double)remaining / goal, 0, 1);
            if (remaining >= 0 && remaining < TimeSpan.MaxValue.TotalMilliseconds)
            {
                var duration = TimeSpan.FromMilliseconds((double)remaining);
                tooltip += $"\nRemaining when checked at {sla.ObservedAt.UtcDateTime:HH:mm:ss} UTC: " +
                    $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
            }
        }
        return new(kind, fraction, tooltip);
    }

    public static DateTimeOffset Deadline(DateOnly dueDate, TimeZoneInfo zone) =>
        WorklogCalendarLayout.LocalDateTime(dueDate.AddDays(1), TimeOnly.MinValue, zone);

    public static DueDateState Calculate(JiraIssue issue, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (issue.DueDate is not { } due || due == DateOnly.MaxValue)
            return new(DueDateKind.None, null, "No due date");
        var deadline = Deadline(due, zone);
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
