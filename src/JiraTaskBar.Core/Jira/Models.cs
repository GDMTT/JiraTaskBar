namespace JiraTaskBar.Core.Jira;

/// <summary>A Jira priority. <see cref="Rank"/> is 1 for the highest priority in the site's priority order.</summary>
public sealed record JiraPriority(string Id, string Name, int Rank);

/// <param name="IssueTypeAvatarId">Numeric avatar ID of the issue type's icon, if Jira gave one.</param>
public sealed record JiraIssue(
    string Key,
    string Summary,
    JiraPriority? Priority,
    string Status,
    string ProjectTypeKey,
    string IssueType,
    string? IssueTypeAvatarId = null)
{
    /// <summary>Jira Service Management projects support internal vs public comments.</summary>
    public bool IsServiceDesk => string.Equals(ProjectTypeKey, "service_desk", StringComparison.OrdinalIgnoreCase);

    public int PriorityRank => Priority?.Rank ?? int.MaxValue;
}

public sealed record JiraUser(string AccountId, string DisplayName, string? EmailAddress);

public sealed record JiraWorklog(
    string Id,
    string IssueKey,
    DateTimeOffset Started,
    int TimeSpentSeconds,
    string Author,
    string? Comment);

/// <param name="SiteUrl">The Jira site, used for browser links (and for REST calls with a classic token).</param>
/// <param name="CloudId">
/// Set for a <b>scoped</b> API token: REST calls then go through Atlassian's API gateway
/// (<c>https://api.atlassian.com/ex/jira/{cloudId}/</c>), the only place scoped tokens are accepted.
/// Null for a classic token, which is used against the site URL.
/// </param>
public sealed record JiraConnection(Uri SiteUrl, string Email, string ApiToken, string? CloudId = null)
{
    /// <summary>The minimum classic scopes a scoped token needs for this app.</summary>
    public const string RequiredScopes = "read:jira-work, write:jira-work, read:jira-user";

    public const string GatewayHost = "api.atlassian.com";

    public bool IsScoped => CloudId is not null;

    /// <summary>Base address for REST calls: the API gateway for scoped tokens, otherwise the site.</summary>
    public Uri ApiBaseUrl => CloudId is null ? SiteUrl : new Uri($"https://{GatewayHost}/ex/jira/{NormalizeCloudId(CloudId)}/");

    public Uri BrowseUrl(string issueKey) => new(SiteUrl, $"/browse/{Uri.EscapeDataString(issueKey)}");

    /// <summary>Validates a cloud ID (a GUID) and returns it in canonical lower-case form.</summary>
    /// <exception cref="ArgumentException">Not a GUID, so it could inject path segments into the gateway URL.</exception>
    public static string NormalizeCloudId(string input) =>
        Guid.TryParse((input ?? string.Empty).Trim(), out var id)
            ? id.ToString("D")
            : throw new ArgumentException("The cloud ID should look like 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d.", nameof(input));

    private const string CloudDomain = ".atlassian.net";

    /// <summary>
    /// Normalises user input such as "mycompany", "mycompany.atlassian.net" or a full URL with a path.
    /// Only https Jira Cloud sites are accepted, because the API token is sent to this host on every request.
    /// </summary>
    /// <exception cref="ArgumentException">The input is not an https://&lt;name&gt;.atlassian.net site.</exception>
    public static Uri NormalizeSiteUrl(string input)
    {
        var text = (input ?? string.Empty).Trim().TrimEnd('/');
        if (text.Length == 0)
            throw new ArgumentException("Jira site URL is required.", nameof(input));
        if (!text.Contains('.') && !text.Contains("://"))
            text += CloudDomain;
        if (!text.Contains("://"))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{input}' isn't a valid site address.", nameof(input));
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The Jira site must use https://.", nameof(input));
        if (!uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new ArgumentException("Enter just the Jira site address, e.g. yourcompany.atlassian.net.", nameof(input));

        var host = uri.IdnHost.ToLowerInvariant();
        var site = host.EndsWith(CloudDomain, StringComparison.Ordinal) ? host[..^CloudDomain.Length] : string.Empty;
        if (site.Length == 0 || site.Contains('.'))
            throw new ArgumentException("Only Jira Cloud sites (yourcompany.atlassian.net) are supported.", nameof(input));

        return new Uri($"https://{host}/");
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
