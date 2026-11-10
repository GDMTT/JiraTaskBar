using System.Text.RegularExpressions;

namespace JiraTaskBar.Core.Jira;

public enum DueDateFilter { Any, HasDueDate, NoDueDate, Overdue }

public sealed record JiraDurationSettings(decimal HoursPerDay, decimal DaysPerWeek)
{
    public static JiraDurationSettings Default { get; } = new(8, 5);
    public long DaySeconds => checked((long)decimal.Round(HoursPerDay * 3600, 0, MidpointRounding.AwayFromZero));
    public long WeekSeconds => checked((long)decimal.Round(DaysPerWeek * DaySeconds, 0, MidpointRounding.AwayFromZero));
}

public static partial class JiraDuration
{
    [GeneratedRegex(@"(\d+)\s*([wdhm])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartRegex();

    public static bool TryParse(string text, JiraDurationSettings settings, out long? seconds)
    {
        seconds = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var value = text.Trim();
        long total = 0;
        var position = 0;
        try
        {
            foreach (Match match in PartRegex().Matches(value))
            {
                if (match.Index != position && !string.IsNullOrWhiteSpace(value[position..match.Index])) return false;
                var amount = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var unit = char.ToLowerInvariant(match.Groups[2].Value[0]);
                var unitSeconds = unit switch
                {
                    'w' => settings.WeekSeconds,
                    'd' => settings.DaySeconds,
                    'h' => 3600,
                    _ => 60,
                };
                total = checked(total + checked(amount * unitSeconds));
                position = match.Index + match.Length;
            }
        }
        catch (Exception ex) when (ex is OverflowException or FormatException) { return false; }
        if (position == 0 || !string.IsNullOrWhiteSpace(value[position..])) return false;
        seconds = total;
        return true;
    }

    public static string Format(long? seconds, JiraDurationSettings settings)
    {
        if (seconds is null) return string.Empty;
        var remaining = Math.Max(0, seconds.Value);
        var parts = new List<string>();
        void Take(long unitSeconds, string suffix)
        {
            if (unitSeconds <= 0 || remaining < unitSeconds) return;
            var count = remaining / unitSeconds;
            remaining %= unitSeconds;
            parts.Add($"{count}{suffix}");
        }
        Take(settings.WeekSeconds, "w");
        Take(settings.DaySeconds, "d");
        Take(3600, "h");
        Take(60, "m");
        if (parts.Count == 0) parts.Add("0m");
        return string.Join(" ", parts);
    }

    public static bool TryParsePreservingOriginal(string text, string displayed, long? original,
        JiraDurationSettings settings, out long? seconds)
    {
        if (string.Equals(text.Trim(), displayed, StringComparison.OrdinalIgnoreCase))
        {
            seconds = original;
            return true;
        }
        return TryParse(text, settings, out seconds);
    }
}

public sealed record TicketFilter
{
    public IReadOnlyList<string> Types { get; init; } = [];
    public string Key { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public long? MinimumDueSeconds { get; init; }
    public long? MaximumDueSeconds { get; init; }
    public DueDateFilter DueDate { get; init; }
    public bool UsesCurrentTime => MinimumDueSeconds.HasValue || MaximumDueSeconds.HasValue || DueDate == DueDateFilter.Overdue;
    public bool IsActive => Types.Count > 0 || !string.IsNullOrWhiteSpace(Key) ||
        !string.IsNullOrWhiteSpace(Summary) || MinimumDueSeconds.HasValue || MaximumDueSeconds.HasValue || DueDate != DueDateFilter.Any;

    public bool Matches(JiraIssue issue) => Matches(issue, DateTimeOffset.UtcNow, TimeZoneInfo.Local);

    public bool Matches(JiraIssue issue, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (Types.Count > 0 && !Types.Contains(issue.IssueType, StringComparer.OrdinalIgnoreCase)) return false;
        if (!issue.Key.Contains(Key.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (Summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(word => !issue.Summary.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;

        if (issue.DueDate is not { } due || due == DateOnly.MaxValue)
            return (DueDate == DueDateFilter.Any || DueDate == DueDateFilter.NoDueDate) &&
                   !MinimumDueSeconds.HasValue && !MaximumDueSeconds.HasValue;

        var remaining = (DueDateProgress.Deadline(due, zone) - now).TotalSeconds;
        if (DueDate == DueDateFilter.NoDueDate) return false;
        if (DueDate == DueDateFilter.Overdue && remaining > 0) return false;
        if (MinimumDueSeconds.HasValue && remaining < MinimumDueSeconds) return false;
        if (MaximumDueSeconds.HasValue && remaining > MaximumDueSeconds) return false;
        return true;
    }
}
