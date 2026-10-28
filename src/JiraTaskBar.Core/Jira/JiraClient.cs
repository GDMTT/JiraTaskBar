using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace JiraTaskBar.Core.Jira;

public interface IJiraClient
{
    JiraConnection Connection { get; }
    Task<JiraUser> GetMyselfAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default);
    Task AddWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, string? comment = null, CancellationToken ct = default);
    Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default);
    /// <returns>The filename Jira stored the attachment under.</returns>
    Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default);
}

/// <summary>Jira Cloud REST client using email + API token (Basic auth).</summary>
public sealed class JiraClient : IJiraClient
{
    private const string SearchFields = "summary,priority,status,project,issuetype";
    private const int MaxIssues = 200;
    /// <summary>Retries after HTTP 429/503 before giving up (worklogs are then queued for later).</summary>
    public const int MaxThrottleRetries = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="http">Should come from <see cref="CreateHttpClient"/> so credentials never follow a redirect.</param>
    /// <param name="delay">Waits between throttled retries; replaceable in tests.</param>
    public JiraClient(HttpClient http, JiraConnection connection, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        // Defence in depth: never attach Basic credentials to a non-TLS request.
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
        var node = await SendAsync(HttpMethod.Get, "rest/api/3/myself", null, ct);
        return new JiraUser(
            node?["accountId"]?.GetValue<string>() ?? string.Empty,
            node?["displayName"]?.GetValue<string>() ?? string.Empty,
            node?["emailAddress"]?.GetValue<string>());
    }

    public async Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await SendAsync(HttpMethod.Get, "rest/api/3/priority", null, ct);
            return JiraPayloads.ParsePriorities(node);
        }
        catch (JiraApiException ex) when (ex.StatusCode is 404 or 410)
        {
            // The flat list is deprecated; the paged replacement returns the same order.
            var node = await SendAsync(HttpMethod.Get, "rest/api/3/priority/search?maxResults=100", null, ct);
            return JiraPayloads.ParsePriorities(node);
        }
    }

    public async Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default)
    {
        var byId = priorities.ToDictionary(p => p.Id);
        var all = new List<JiraIssue>();
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
        } while (token is not null && all.Count < MaxIssues);

        return IssueOrdering.Sort(all);
    }

    public Task AddWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, string? comment = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/worklog",
            JiraPayloads.Worklog(startedLocal, timeSpentSeconds, comment), ct);

    public Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}/comment",
            JiraPayloads.Comment(wikiMarkup, isInternal), ct);

    public async Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default)
    {
        var node = await SendRequestAsync(() =>
        {
            var content = new MultipartFormDataContent();
            var file = new StreamContent(File.OpenRead(filePath));
            file.Headers.ContentType = new MediaTypeHeaderValue(MimeType(filePath));
            content.Add(file, "file", Path.GetFileName(filePath));

            var request = new HttpRequestMessage(HttpMethod.Post, $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/attachments")
            {
                Content = content,
            };
            request.Headers.Add("X-Atlassian-Token", "no-check");
            return request;
        }, ct);
        return (node as JsonArray)?.FirstOrDefault()?["filename"]?.GetValue<string>() ?? Path.GetFileName(filePath);
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
    private async Task<JsonNode?> SendRequestAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            using var response = await _http.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (status is 429 or 503 && attempt < MaxThrottleRetries)
            {
                await _delay(RetryDelay(response, attempt), ct);
                continue;
            }
            return await ReadResponseAsync(response, ct);
        }
    }

    /// <summary>Honours Retry-After (seconds or date), otherwise exponential back-off: 2s, 4s, 8s.</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? requested = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        var delay = requested is { } r && r > TimeSpan.Zero ? r : TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    private static async Task<JsonNode?> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
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
