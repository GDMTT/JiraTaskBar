namespace JiraTaskBar.Core.Jira;

/// <summary>A Jira priority. <see cref="Rank"/> is 1 for the highest priority in the site's priority order.</summary>
public sealed record JiraPriority(string Id, string Name, int Rank);

public sealed record JiraIssue(
    string Key,
    string Summary,
    JiraPriority? Priority,
    string Status,
    string ProjectTypeKey,
    string IssueType)
{
    /// <summary>Jira Service Management projects support internal vs public comments.</summary>
    public bool IsServiceDesk => string.Equals(ProjectTypeKey, "service_desk", StringComparison.OrdinalIgnoreCase);

    public int PriorityRank => Priority?.Rank ?? int.MaxValue;
}

public sealed record JiraUser(string AccountId, string DisplayName, string? EmailAddress);

public sealed record JiraConnection(Uri SiteUrl, string Email, string ApiToken)
{
    public Uri BrowseUrl(string issueKey) => new(SiteUrl, $"/browse/{Uri.EscapeDataString(issueKey)}");

    /// <summary>Normalises user input such as "mycompany", "mycompany.atlassian.net" or a full URL with a path.</summary>
    public static Uri NormalizeSiteUrl(string input)
    {
        var text = (input ?? string.Empty).Trim().TrimEnd('/');
        if (text.Length == 0)
            throw new ArgumentException("Jira site URL is required.", nameof(input));
        if (!text.Contains('.') && !text.Contains("://"))
            text += ".atlassian.net";
        if (!text.Contains("://"))
            text = "https://" + text;
        var uri = new Uri(text, UriKind.Absolute);
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }
}

public sealed class JiraApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Sorts issues so priority 1 comes first, then by key in natural order (ABC-2 before ABC-10).</summary>
public static class IssueOrdering
{
    public static IReadOnlyList<JiraIssue> Sort(IEnumerable<JiraIssue> issues) =>
        issues.OrderBy(i => i.PriorityRank)
              .ThenBy(i => ProjectPart(i.Key), StringComparer.OrdinalIgnoreCase)
              .ThenBy(i => NumberPart(i.Key))
              .ToList();

    private static string ProjectPart(string key)
    {
        var dash = key.LastIndexOf('-');
        return dash < 0 ? key : key[..dash];
    }

    private static long NumberPart(string key)
    {
        var dash = key.LastIndexOf('-');
        return dash >= 0 && long.TryParse(key[(dash + 1)..], out var n) ? n : 0;
    }
}
