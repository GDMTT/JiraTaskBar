using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.Core.Jira;

public enum DueDateKind { None, Upcoming, Soon, Today, Overdue, Met }
public sealed record DueDateState(DueDateKind Kind, double? RemainingFraction, string Tooltip);

public static class DueDateProgress
{
    public static DueDateState CalculateUrgency(JiraIssue issue, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (issue.Sla is not { } sla) return Calculate(issue, now, zone);

        var title = $"{sla.Name} (SLA)";
        if (sla.Breached) return new(DueDateKind.Overdue, 0, $"{title}: Breached");
        if (sla.Completed) return new(DueDateKind.Met, 1, $"{title}: Met");
        if (now - sla.ObservedAt >= TimeSpan.FromMinutes(10))
            return new(DueDateKind.Upcoming, null, $"{title}: Waiting for a fresh Jira reading");

        var status = sla.Paused ? "Paused" : "In progress";
        var tooltip = $"{title}: {status}";
        if (!sla.Paused && issue.CreatedAt is { } created && sla.BreachAt is { } deadline && created < deadline)
        {
            var timeLeft = deadline > now ? deadline - now : TimeSpan.Zero;
            var progress = Math.Clamp(timeLeft.TotalSeconds / (deadline - created).TotalSeconds, 0, 1);
            return new(DueDateKind.Upcoming, progress,
                $"{tooltip}\nEstimated remaining to the resolution deadline: {FormatCountdown(timeLeft)}\n" +
                $"{progress:P0} of the creation-to-deadline interval remains");
        }

        if (sla.RemainingMilliseconds is not { } snapshot) return new(DueDateKind.Upcoming, null, tooltip);

        var remaining = Math.Max(0d, snapshot);
        var label = $"Remaining when checked at {sla.ObservedAt.UtcDateTime:HH:mm:ss} UTC";
        if (snapshot > 0 && sla.ContinuousDeadline is { } breach)
        {
            // A projected deadline that agrees with the SLA clock indicates a continuous working interval.
            // The tolerance allows for Jira's sampling and timestamp precision.
            remaining = Math.Clamp((breach - now).TotalMilliseconds, 0, remaining);
            label = "Estimated remaining";
        }
        double? fraction = sla.GoalMilliseconds is > 0 and var goal ? Math.Clamp(remaining / goal, 0, 1) : null;
        if (remaining < TimeSpan.MaxValue.TotalMilliseconds)
        {
            var duration = TimeSpan.FromMilliseconds(remaining);
            tooltip += $"\n{label}: {FormatCountdown(duration)}";
            if (fraction is { } progress) tooltip += $" ({progress:P0} of the SLA goal)";
        }
        return new(DueDateKind.Upcoming, fraction, tooltip);
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
        var countdown = FormatCountdown(duration);
        return new(kind, fraction, $"{label}: {due:yyyy-MM-dd} (end of day, {zone.DisplayName})\n" +
            (kind == DueDateKind.Overdue ? $"Overdue by {countdown}" : $"Remaining: {countdown}") +
            (fraction is { } progress ? $"\n{progress:P0} of the creation-to-deadline interval remains" : "\nPercentage unavailable: no valid creation baseline"));
    }

    private static string FormatCountdown(TimeSpan duration) =>
        $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
}
