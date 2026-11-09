using System.Net;
using System.Text;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.Core.Tests;

public sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string? Body, IReadOnlyDictionary<string, string> Headers, Uri Url);

/// <summary>Answers requests from a queue of canned responses and records what was sent.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, Func<HttpContent> Content, TimeSpan? RetryAfter)> _responses = new();
    public List<RecordedRequest> Requests { get; } = [];

    public FakeHandler Respond(string json, HttpStatusCode status = HttpStatusCode.OK, TimeSpan? retryAfter = null)
    {
        _responses.Enqueue((status, () => new StringContent(json, Encoding.UTF8, "application/json"), retryAfter));
        return this;
    }

    public FakeHandler RespondBytes(byte[] body, string contentType)
    {
        _responses.Enqueue((HttpStatusCode.OK, () =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return content;
        }, null));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, body, headers, request.RequestUri));
        var (status, content, retryAfter) = _responses.Count > 0
            ? _responses.Dequeue()
            : (HttpStatusCode.OK, () => new StringContent("{}", Encoding.UTF8, "application/json"), null);
        var response = new HttpResponseMessage(status) { Content = content() };
        if (retryAfter is { } delay)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        return response;
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JiraTaskBarTests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public sealed class FakeJiraClient : IJiraClient
{
    public JiraConnection Connection { get; } = new(new Uri("https://example.atlassian.net/"), "me@example.com", "token");
    public List<(string Key, DateTimeOffset Started, int Seconds)> Worklogs { get; } = [];
    public int FailuresRemaining { get; set; }

    public Task AddWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, string? comment = null, CancellationToken ct = default)
    {
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new HttpRequestException("network down");
        }
        Worklogs.Add((issueKey, startedLocal, timeSpentSeconds));
        return Task.CompletedTask;
    }

    public Task UpdateWorklogAsync(string issueKey, JiraWorklog worklog, DateTimeOffset startedLocal,
        int timeSpentSeconds, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<JiraUser> GetMyselfAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<JiraDurationSettings> GetTimeTrackingConfigurationAsync(CancellationToken ct = default) =>
        Task.FromResult(JiraDurationSettings.Default);
    public Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<JiraWorklog>> GetWorklogsAsync(string issueKey, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<byte[]?> GetIssueTypeIconAsync(string avatarId, CancellationToken ct = default) => throw new NotSupportedException();
}
