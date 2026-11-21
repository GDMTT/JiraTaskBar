using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JiraTaskBar.Core.Jira;

public interface IJiraClient
{
    JiraConnection Connection { get; }
    Task<JiraUser> GetMyselfAsync(CancellationToken ct = default);
    Task<JiraDurationSettings> GetTimeTrackingConfigurationAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default);
    Task<IReadOnlyList<JiraIssue>> BrowseIssuesAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) =>
        SearchAsync(jql, priorities, ct);
    Task<IReadOnlyList<JiraQueue>> GetQueuesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JiraQueue>>([]);
    Task<IReadOnlyList<JiraIssueLookup>> FetchIssuesByKeyAsync(IReadOnlyList<string> keys,
        IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default);
    Task<IReadOnlyList<JiraIssue>> FindWorklogIssuesAsync(string accountId, DateOnly until, CancellationToken ct = default);
    Task<JiraSla?> GetRequestSlaAsync(string issueKey, CancellationToken ct = default);
    Task<IReadOnlyList<JiraWorklog>> GetWorklogsAsync(string issueKey, CancellationToken ct = default);
    Task AddWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, string? comment = null, CancellationToken ct = default);
    Task UpdateWorklogAsync(string issueKey, JiraWorklog worklog, DateTimeOffset startedLocal,
        int timeSpentSeconds, CancellationToken ct = default);
    Task DeleteWorklogAsync(string issueKey, string worklogId, CancellationToken ct = default);
    Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default);
    Task AddWorklogCommentAsync(string issueKey, string worklogId, string wikiMarkup, CancellationToken ct = default);
    Task<string?> AddWorklogWithIdAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, CancellationToken ct = default);
    /// <returns>The filename Jira stored the attachment under.</returns>
    Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default);
    /// <summary>The issue type's icon as PNG bytes, or null if Jira didn't return a usable PNG.</summary>
    Task<byte[]?> GetIssueTypeIconAsync(string avatarId, CancellationToken ct = default);
}

/// <summary>REST client for Jira Cloud and Jira Data Center.</summary>
public sealed class JiraClient : IJiraClient
{
    private const string SearchFields = "summary,priority,status,project,issuetype,timeestimate,created,duedate";
    private const int MaxIssues = 200;
    /// <summary>Retries after HTTP 429/503 before giving up (worklogs are then queued for later).</summary>
    public const int MaxThrottleRetries = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private string ApiPath => Connection.IsDataCenter ? "rest/api/2" : "rest/api/3";

    /// <param name="http">Should come from <see cref="CreateHttpClient"/> so credentials never follow a redirect.</param>
    /// <param name="delay">Waits between throttled retries; replaceable in tests.</param>
    public JiraClient(HttpClient http, JiraConnection connection, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        // Defence in depth: never attach Basic credentials to a non-TLS request.
        if (connection.IsDataCenter)
        {
            if (!connection.SiteUrl.IsAbsoluteUri || connection.CloudId is not null || string.IsNullOrWhiteSpace(connection.Email) ||
                connection.Email.Contains(':') || connection.ApiToken.Length == 0)
                throw new ArgumentException("Enter a Jira Data Center username and password.", nameof(connection));
            connection = connection with { SiteUrl = JiraConnection.NormalizeConnectionSite(connection.SiteUrl.OriginalString, true) };
        }
        var baseUrl = connection.ApiBaseUrl;
        if (baseUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The Jira site must use https://.", nameof(connection));

        _http = http;
        _delay = delay ?? Task.Delay;
        Connection = connection;
        _http.BaseAddress = baseUrl;
        var raw = Encoding.UTF8.GetBytes($"{connection.Email}:{connection.ApiToken}");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("JiraTaskBar/1.0");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public JiraConnection Connection { get; }

    /// <summary>
    /// An HttpClient that does not follow redirects. A redirect from the Jira API is unexpected,
    /// and following it could carry the Authorization header to another host.
    /// </summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    /// <summary>
    /// Looks up a site's cloud ID (needed for scoped tokens) from <c>/_edge/tenant_info</c>.
    /// Sent without credentials. The endpoint isn't a documented API, so failure returns null
    /// and the user can paste the ID from admin.atlassian.com instead.
    /// </summary>
    /// <param name="http">A client with no default Authorization header.</param>
    public static async Task<string?> TryGetCloudIdAsync(HttpClient http, Uri site, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(site, "/_edge/tenant_info"));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var id = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["cloudId"]?.GetValue<string>();
            return id is null ? null : JiraConnection.NormalizeCloudId(id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<JiraUser> GetMyselfAsync(CancellationToken ct = default)
    {
        JsonObject? node;
        try { node = await SendAsync(HttpMethod.Get, $"{ApiPath}/myself", null, ct) as JsonObject; }
        catch (JiraApiException ex) when (Connection.IsDataCenter && ex.StatusCode == 403)
        {
            throw new JiraApiException("Jira denied REST Basic sign-in. Check with your Jira administrator that this authentication method is allowed.", 401);
        }
        var account = node?[Connection.IsDataCenter ? "name" : "accountId"] is JsonValue value &&
            value.TryGetValue<string>(out var login) ? login : null;
        if (Connection.IsDataCenter && string.IsNullOrWhiteSpace(account))
            throw new JiraApiException("Jira did not confirm the username. Check the base URL and whether REST Basic sign-in is enabled.", 401);
        return new JiraUser(
            account ?? string.Empty,
            node?["displayName"]?.GetValue<string>() ?? string.Empty,
            node?["emailAddress"]?.GetValue<string>());
    }

    public async Task<JiraDurationSettings> GetTimeTrackingConfigurationAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await SendAsync(HttpMethod.Get, $"{ApiPath}/configuration", null, ct);
            var config = node?["timeTrackingConfiguration"] ?? node;
            var hours = config?["workingHoursPerDay"]?.GetValue<decimal>() ?? JiraDurationSettings.Default.HoursPerDay;
            var days = config?["workingDaysPerWeek"]?.GetValue<decimal>() ?? JiraDurationSettings.Default.DaysPerWeek;
            return hours is > 0 and <= 24 && days is > 0 and <= 7
                ? new JiraDurationSettings(hours, days)
                : JiraDurationSettings.Default;
        }
        catch (Exception ex) when (ex is JiraApiException { StatusCode: 403 or 404 }
                                   or System.Text.Json.JsonException or InvalidOperationException)
        {
            return JiraDurationSettings.Default;
        }
    }

    public async Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await SendAsync(HttpMethod.Get, $"{ApiPath}/priority", null, ct);
            return JiraPayloads.ParsePriorities(node);
        }
        catch (JiraApiException ex) when (!Connection.IsDataCenter && ex.StatusCode is 404 or 410)
        {
            // The flat list is deprecated; the paged replacement returns the same order.
            var node = await SendAsync(HttpMethod.Get, "rest/api/3/priority/search?maxResults=100", null, ct);
            return JiraPayloads.ParsePriorities(node);
        }
    }

    public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) =>
        SearchIssuesAsync(jql, priorities, MaxIssues, ct);

    public Task<IReadOnlyList<JiraIssue>> BrowseIssuesAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) =>
        SearchIssuesAsync(jql, priorities, null, ct);

    public async Task<IReadOnlyList<JiraQueue>> GetQueuesAsync(CancellationToken ct = default)
    {
        const string path = "rest/servicedeskapi/servicedesk";
        var desks = await ReadServiceDeskPagesAsync(path, ct);
        var queues = new List<JiraQueue>();
        foreach (var desk in desks)
        {
            var id = desk["id"]?.GetValue<string>();
            var project = desk["projectKey"]?.GetValue<string>() ?? desk["projectName"]?.GetValue<string>();
            if (!IsNumericId(id) || string.IsNullOrWhiteSpace(project)) continue;
            try
            {
                foreach (var queue in await ReadServiceDeskPagesAsync($"{path}/{id}/queue", ct))
                {
                    var name = queue["name"]?.GetValue<string>();
                    var jql = queue["jql"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(jql)) queues.Add(new(project, name, jql));
                }
            }
            catch (JiraApiException ex) when (ex.StatusCode is 403 or 404)
            {
                // A service project can be visible to customers who have no queue access.
            }
        }
        return queues.OrderBy(queue => queue.Project, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(queue => queue.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<JsonObject>> ReadServiceDeskPagesAsync(string path, CancellationToken ct)
    {
        var result = new List<JsonObject>();
        for (var start = 0; ;)
        {
            var response = await SendAsync(HttpMethod.Get, $"{path}?start={start}&limit=50", null, ct);
            if (response?["values"] is not JsonArray { Count: > 0 } values) return result;
            result.AddRange(values.OfType<JsonObject>());
            if (response["isLastPage"]?.GetValue<bool>() != false) return result;
            var pageStart = response["start"]?.GetValue<int>() ?? start;
            if (pageStart != start) throw new InvalidOperationException("Jira returned an unexpected queue page offset.");
            start = checked(start + values.Count);
        }
    }

    public async Task<IReadOnlyList<JiraIssueLookup>> FetchIssuesByKeyAsync(IReadOnlyList<string> keys,
        IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default)
    {
        var byId = priorities.ToDictionary(priority => priority.Id);
        var requested = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new JiraIssueLookup[requested.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, requested.Length), options, async (index, token) =>
        {
            var key = requested[index];
            try
            {
                var response = await SendAsync(HttpMethod.Get, $"{ApiPath}/issue/{Uri.EscapeDataString(key)}?fields={SearchFields}", null, token);
                var issue = response is JsonObject ? JiraPayloads.ParseIssue(response, byId) : null;
                results[index] = string.IsNullOrWhiteSpace(issue?.Key) ? new(key, null, CanRetry: true) : new(key, issue);
            }
            catch (JiraApiException ex) when (ex.StatusCode is 400 or 403 or 404 or 410)
            {
                results[index] = new(key, null);
            }
            catch (Exception ex) when (ex is JiraApiException { StatusCode: not 401 } or HttpRequestException or JsonException ||
                                       ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                results[index] = new(key, null, CanRetry: true);
            }
        });
        return results;
    }

    public Task<IReadOnlyList<JiraIssue>> FindWorklogIssuesAsync(string accountId, DateOnly until,
        CancellationToken ct = default)
    {
        var author = accountId.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var jql = FormattableString.Invariant($"worklogAuthor = \"{author}\" AND worklogDate < \"{until:yyyy-MM-dd}\"");
        return SearchIssuesAsync(jql, [], null, ct);
    }

    private async Task<IReadOnlyList<JiraIssue>> SearchIssuesAsync(string jql, IReadOnlyList<JiraPriority> priorities,
        int? maxIssues, CancellationToken ct)
    {
        var byId = priorities.ToDictionary(p => p.Id);
        if (Connection.IsDataCenter) return await SearchDataCenterAsync(jql, byId, maxIssues, ct);
        var all = new List<JiraIssue>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var url = $"rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&fields={SearchFields}&maxResults=100";
            if (token is not null)
                url += "&nextPageToken=" + Uri.EscapeDataString(token);
            var node = await SendAsync(HttpMethod.Get, url, null, ct);
            var (issues, next) = JiraPayloads.ParseSearch(node, byId);
            all.AddRange(issues);
            token = next;
            if (token is not null && !seenTokens.Add(token))
                throw new InvalidOperationException("Jira returned a repeated issue search page token.");
        } while (token is not null && (maxIssues is null || all.Count < maxIssues));

        return IssueOrdering.Sort(all);
    }

    private async Task<IReadOnlyList<JiraIssue>> SearchDataCenterAsync(string jql,
        IReadOnlyDictionary<string, JiraPriority> priorities, int? maxIssues, CancellationToken ct)
    {
        var all = new List<JiraIssue>();
        for (var offset = 0; maxIssues is null || all.Count < maxIssues;)
        {
            var body = new JsonObject { ["jql"] = jql, ["startAt"] = offset, ["maxResults"] = 100,
                ["fields"] = new JsonArray(SearchFields.Split(',').Select(field => (JsonNode?)JsonValue.Create(field)).ToArray()) };
            var page = await SendAsync(HttpMethod.Post, $"{ApiPath}/search", body, ct) as JsonObject;
            if (page?["issues"] is not JsonArray issues || page["startAt"]?.GetValue<int>() != offset ||
                page["total"]?.GetValue<int>() is not { } total)
                throw new InvalidOperationException("Jira returned an invalid issue search page.");
            all.AddRange(issues.OfType<JsonObject>().Select(issue => JiraPayloads.ParseIssue(issue, priorities)));
            offset += issues.Count;
            if (issues.Count == 0 || offset >= total) break;
        }
        return IssueOrdering.Sort(all);
    }

    public async Task<JiraSla?> GetRequestSlaAsync(string issueKey, CancellationToken ct = default)
    {
        var path = $"rest/servicedeskapi/request/{Uri.EscapeDataString(issueKey)}/sla";
        for (var start = 0; start < 1000;)
        {
            var response = await SendAsync(HttpMethod.Get, $"{path}?start={start}&limit=50", null, ct) as JsonObject;
            if (response?["values"] is not JsonArray { Count: > 0 } values) return null;
            foreach (var metric in values.OfType<JsonObject>())
                if (JiraSla.Parse(metric) is { } sla) return sla with { ObservedAt = DateTimeOffset.UtcNow };

            if (response["isLastPage"] is not JsonValue last || !last.TryGetValue<bool>(out var isLast) || isLast)
                return null;
            start += values.Count;
        }
        return null;
    }

    public Task AddWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, string? comment = null, CancellationToken ct = default) =>
        PostWorklogAsync(issueKey, startedLocal, timeSpentSeconds, comment, ct);

    public async Task<string?> AddWorklogWithIdAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, CancellationToken ct = default)
    {
        var response = await PostWorklogAsync(issueKey, startedLocal, timeSpentSeconds, null, ct);
        return (response as JsonObject)?["id"] is JsonValue value &&
            value.TryGetValue<string>(out var id) && IsNumericId(id) ? id : null;
    }

    private Task<JsonNode?> PostWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds,
        string? comment, CancellationToken ct) => SendAsync(HttpMethod.Post,
        $"{ApiPath}/issue/{Uri.EscapeDataString(issueKey)}/worklog",
        JiraPayloads.Worklog(startedLocal, timeSpentSeconds, comment, usePlainText: Connection.IsDataCenter), ct);

    public Task UpdateWorklogAsync(string issueKey, JiraWorklog worklog, DateTimeOffset startedLocal,
        int timeSpentSeconds, CancellationToken ct = default)
    {
        return SendAsync(HttpMethod.Put,
            WorklogPath(issueKey, worklog.Id),
            JiraPayloads.Worklog(startedLocal, timeSpentSeconds, worklog.Comment, worklog.CommentDocument,
                usePlainText: Connection.IsDataCenter), ct);
    }

    public async Task DeleteWorklogAsync(string issueKey, string worklogId, CancellationToken ct = default)
    {
        var worklogPath = WorklogPath(issueKey, worklogId);
        var commentPath = CommentPath(issueKey);
        var linked = await GetLinkedWorklogCommentIdsAsync(commentPath, worklogId, ct);
        var removed = 0;
        try
        {
            // Keep the worklog available for retry if Jira rejects removal of a linked comment.
            foreach (var id in linked)
            {
                await SendAsync(HttpMethod.Delete, $"{commentPath}/{id}", null, ct);
                removed++;
            }
            await SendAsync(HttpMethod.Delete, $"{worklogPath}?adjustEstimate=leave", null, ct);
        }
        catch (Exception ex) when (removed > 0 && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Jira deleted {removed} linked comment(s) before the operation stopped. " +
                $"Refresh the worklog before retrying. {ex.Message}", ex);
        }
    }

    private async Task<IReadOnlyCollection<string>> GetLinkedWorklogCommentIdsAsync(string commentPath,
        string worklogId, CancellationToken ct)
    {
        var linked = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var startAt = 0;
        int? expectedTotal = null;
        while (true)
        {
            var page = await SendAsync(HttpMethod.Get,
                $"{commentPath}?startAt={startAt}&maxResults=100&orderBy=created&expand=properties", null, ct) as JsonObject;
            if (page?["comments"] is not JsonArray comments || page["total"] is not JsonValue totalValue ||
                !totalValue.TryGetValue<int>(out var total) || total < 0 ||
                page["startAt"] is not JsonValue startValue || !startValue.TryGetValue<int>(out var offset) || offset != startAt ||
                comments.Count > total - startAt || (comments.Count == 0 && startAt < total) ||
                (expectedTotal is { } expected && total != expected))
                throw new InvalidOperationException("Jira returned an incomplete comment list. The worklog was not deleted.");

            expectedTotal = total;
            foreach (var node in comments)
            {
                if (node is not JsonObject comment || comment["id"] is not JsonValue value ||
                    !value.TryGetValue<string>(out var id) || !IsNumericId(id) || !seen.Add(id!))
                    throw new InvalidOperationException("Jira returned an invalid or repeated comment ID. The worklog was not deleted.");
                if (JiraPayloads.IsLinkedWorklogComment(comment, worklogId))
                    linked.Add(id!);
            }
            startAt += comments.Count;
            if (startAt >= total) return linked;
        }
    }

    private string WorklogPath(string issueKey, string worklogId)
    {
        if (!IsNumericId(worklogId))
            throw new ArgumentException("Jira worklog IDs must be numeric.", nameof(worklogId));
        return $"{ApiPath}/issue/{Uri.EscapeDataString(issueKey)}/worklog/{worklogId}";
    }

    private static bool IsNumericId(string? id) => !string.IsNullOrEmpty(id) && id.All(char.IsAsciiDigit);

    public async Task<IReadOnlyList<JiraWorklog>> GetWorklogsAsync(string issueKey, CancellationToken ct = default)
    {
        var key = Uri.EscapeDataString(issueKey);
        var path = $"{ApiPath}/issue/{key}/worklog";
        if (Connection.IsDataCenter)
        {
            var response = await SendAsync(HttpMethod.Get, path, null, ct);
            return JiraPayloads.ParseWorklogs(response, issueKey, isDataCenter: true).Worklogs;
        }

        var all = new List<JiraWorklog>();
        var startAt = 0;
        while (true)
        {
            var node = await SendAsync(HttpMethod.Get, $"{path}?startAt={startAt}&maxResults=100", null, ct);
            var page = JiraPayloads.ParseWorklogs(node, issueKey);
            all.AddRange(page.Worklogs);
            // Jira offsets count every response entry, including malformed entries we skip locally.
            // Advancing by the parsed count can otherwise duplicate valid worklogs indefinitely.
            startAt += page.ReturnedCount;
            if (page.ReturnedCount == 0 || startAt >= page.Total) break;
        }
        return all;
    }

    public Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, CommentPath(issueKey), JiraPayloads.Comment(wikiMarkup, isInternal), ct);

    public Task AddWorklogCommentAsync(string issueKey, string worklogId, string wikiMarkup, CancellationToken ct = default)
    {
        if (!IsNumericId(worklogId)) throw new ArgumentException("Jira worklog IDs must be numeric.", nameof(worklogId));
        return SendAsync(HttpMethod.Post, CommentPath(issueKey),
            JiraPayloads.Comment(wikiMarkup, isInternal: true, worklogId), ct);
    }

    private static string CommentPath(string issueKey) => $"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}/comment";

    public async Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default)
    {
        var node = await SendRequestAsync(() =>
        {
            var content = new MultipartFormDataContent();
            var file = new StreamContent(File.OpenRead(filePath));
            file.Headers.ContentType = new MediaTypeHeaderValue(MimeType(filePath));
            content.Add(file, "file", Path.GetFileName(filePath));

            var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPath}/issue/{Uri.EscapeDataString(issueKey)}/attachments")
            {
                Content = content,
            };
            request.Headers.Add("X-Atlassian-Token", "no-check");
            return request;
        }, ct);
        return (node as JsonArray)?.FirstOrDefault()?["filename"]?.GetValue<string>() ?? Path.GetFileName(filePath);
    }

    /// <summary>Largest icon accepted; Jira's are a few KB.</summary>
    private const int MaxIconBytes = 256 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public Task<byte[]?> GetIssueTypeIconAsync(string avatarId, CancellationToken ct = default)
    {
        // Built from the ID (digits only) against our own API base, never from a URL in the response,
        // so credentials only go to the Jira site / Atlassian gateway.
        if (!JiraPayloads.IsAvatarId(avatarId))
            throw new ArgumentException("Avatar IDs are numeric.", nameof(avatarId));
        var url = Connection.IsDataCenter ? $"secure/viewavatar?size=medium&avatarId={avatarId}&avatarType=issuetype"
            : $"{ApiPath}/universal_avatar/view/type/issuetype/avatar/{avatarId}?size=medium&format=png";
        return SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ReadPngAsync, ct);
    }

    private async Task<byte[]?> ReadPngAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ReadResponseAsync(response, ct); // throws JiraApiException with Jira's message
            return null;
        }
        if (response.Content.Headers.ContentLength > MaxIconBytes) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        // Only real PNGs are handed to the image decoder (Jira may answer with SVG, which WPF can't show).
        return bytes.Length <= MaxIconBytes && bytes.AsSpan().StartsWith(PngSignature) ? bytes : null;
    }

    private Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        var json = body?.ToJsonString();
        return SendRequestAsync(() =>
        {
            var request = new HttpRequestMessage(method, url);
            if (json is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return request;
        }, ct);
    }

    /// <summary>
    /// Sends a request built by <paramref name="createRequest"/> (a request can only be sent once,
    /// so retries build a fresh one). Throttled responses (429/503) are retried with back-off.
    /// </summary>
    private Task<JsonNode?> SendRequestAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct) =>
        SendWithRetryAsync(createRequest, ReadResponseAsync, ct);

    private async Task<T> SendWithRetryAsync<T>(Func<HttpRequestMessage> createRequest,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readResponse, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            using var response = await _http.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (Connection.IsDataCenter) CheckDataCenterSignIn(response, status);
            if (status is 429 or 503 && attempt < MaxThrottleRetries)
            {
                await _delay(RetryDelay(response, attempt), ct);
                continue;
            }
            return await readResponse(response, ct);
        }
    }

    private static void CheckDataCenterSignIn(HttpResponseMessage response, int status)
    {
        if (response.Headers.TryGetValues("X-Seraph-LoginReason", out var reasons) &&
            reasons.Any(reason => reason is "AUTHENTICATION_DENIED" or "AUTHENTICATION_FAILED" or "AUTHENTICATED_FAILED"))
            throw new JiraApiException("Jira blocked REST Basic sign-in. Check your credentials and ask your Jira administrator about CAPTCHA or sign-in policies.", 401);

        if (status is >= 300 and < 400)
            throw new JiraApiException("Jira redirected the request. Check the Data Center base URL and whether REST Basic sign-in is enabled.", 401);

        if (response.IsSuccessStatusCode && string.Equals(response.Content.Headers.ContentType?.MediaType,
            "text/html", StringComparison.OrdinalIgnoreCase))
            throw UnexpectedDataCenterSignInPage();
    }

    private static JiraApiException UnexpectedDataCenterSignInPage() => new(
        "Jira returned a web page for a REST request. Check the Data Center base URL and whether REST Basic sign-in is enabled.", 401);

    /// <summary>Honours Retry-After (seconds or date), otherwise exponential back-off: 2s, 4s, 8s.</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? requested = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        var delay = requested is { } r && r > TimeSpan.Zero ? r : TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    private async Task<JsonNode?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (Connection.IsDataCenter && !response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var message = status switch
            {
                401 => "Jira rejected the username or password. Check whether REST Basic sign-in is enabled.",
                403 => "Jira denied access. Check your permissions and whether REST Basic sign-in is enabled.",
                _ => $"Jira Data Center returned HTTP {status}.",
            };
            throw new JiraApiException(message, status);
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        if (Connection.IsDataCenter && text.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal))
            throw UnexpectedDataCenterSignInPage();
        JsonNode? node = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { node = JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) { /* non-JSON error page */ }
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var fallback = status switch
            {
                >= 300 and < 400 => $"Jira redirected the request ({status}) – check the site address in Settings.",
                401 => "Jira rejected the email or API token (401).",
                403 => "You don't have permission for this action in Jira (403).",
                404 => "Not found in Jira (404).",
                429 => "Jira is rate-limiting requests (429). Try again in a few minutes.",
                _ => $"Jira returned {status} {response.ReasonPhrase}.",
            };
            throw new JiraApiException(JiraPayloads.ErrorMessage(node, fallback), status);
        }
        return node;
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".txt" or ".log" => "text/plain",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
