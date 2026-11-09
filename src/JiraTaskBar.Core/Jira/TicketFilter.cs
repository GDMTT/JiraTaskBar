namespace JiraTaskBar.Core.Jira;

public enum EstimateFilter { Any, HasEstimate, NoEstimate }

public sealed record TicketFilter
{
    public static bool TryMinuteBound(string text, bool minimum, out int? seconds)
    {
        seconds = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.CurrentCulture, out var minutes) || minutes < 0 || minutes > int.MaxValue / 60m) return false;
        seconds = (int)(minimum ? decimal.Ceiling(minutes * 60) : decimal.Floor(minutes * 60));
        return true;
    }
    public IReadOnlyList<string> Types { get; init; } = [];
    public string Key { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public int? MinimumSeconds { get; init; }
    public int? MaximumSeconds { get; init; }
    public EstimateFilter Estimate { get; init; }
    public bool IsActive => Types.Count > 0 || !string.IsNullOrWhiteSpace(Key) ||
        !string.IsNullOrWhiteSpace(Summary) || MinimumSeconds.HasValue || MaximumSeconds.HasValue || Estimate != EstimateFilter.Any;

    public bool Matches(JiraIssue issue)
    {
        if (Types.Count > 0 && !Types.Contains(issue.IssueType, StringComparer.OrdinalIgnoreCase)) return false;
        if (!issue.Key.Contains(Key.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (Summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(word => !issue.Summary.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        var remaining = issue.RemainingEstimateSeconds;
        if (Estimate == EstimateFilter.NoEstimate && remaining.HasValue) return false;
        if (Estimate == EstimateFilter.HasEstimate && !remaining.HasValue) return false;
        if ((MinimumSeconds.HasValue || MaximumSeconds.HasValue) && !remaining.HasValue) return false;
        return (!MinimumSeconds.HasValue || remaining >= MinimumSeconds) &&
            (!MaximumSeconds.HasValue || remaining <= MaximumSeconds);
    }
}
