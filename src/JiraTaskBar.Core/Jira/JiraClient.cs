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
    Task AddCommentAsync(string issueKey, string wikiMarkup, bool? isInternal, CancellationToken ct = default);
    /// <returns>The filename Jira stored the attachment under.</returns>
    Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default);
}

/// <summary>Jira Cloud REST client using email + API token (Basic auth).</summary>
public sealed class JiraClient : IJiraClient
{
    private const string SearchFields = "summary,priority,status,project,issuetype";
    private const int MaxIssues = 200;

    private readonly HttpClient _http;

    public JiraClient(HttpClient http, JiraConnection connection)
    {
        _http = http;
        Connection = connection;
        _http.BaseAddress = connection.SiteUrl;
        var raw = Encoding.UTF8.GetBytes($"{connection.Email}:{connection.ApiToken}");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("JiraTaskBar/1.0");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public JiraConnection Connection { get; }

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

    public Task AddCommentAsync(string issueKey, string wikiMarkup, bool? isInternal, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}/comment",
            JiraPayloads.Comment(wikiMarkup, isInternal), ct);

    public async Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(File.OpenRead(filePath));
        file.Headers.ContentType = new MediaTypeHeaderValue(MimeType(filePath));
        content.Add(file, "file", Path.GetFileName(filePath));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/attachments")
        {
            Content = content,
        };
        request.Headers.Add("X-Atlassian-Token", "no-check");
        var node = await SendRequestAsync(request, ct);
        return (node as JsonArray)?.FirstOrDefault()?["filename"]?.GetValue<string>() ?? Path.GetFileName(filePath);
    }

    private Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return SendRequestAsync(request, ct);
    }

    private async Task<JsonNode?> SendRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await _http.SendAsync(request, ct))
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
                    401 => "Jira rejected the email or API token (401).",
                    403 => "You don't have permission for this action in Jira (403).",
                    404 => "Not found in Jira (404).",
                    _ => $"Jira returned {status} {response.ReasonPhrase}.",
                };
                throw new JiraApiException(JiraPayloads.ErrorMessage(node, fallback), status);
            }
            return node;
        }
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
