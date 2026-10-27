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

public sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string? Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>Answers requests from a queue of canned responses and records what was sent.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
    public List<RecordedRequest> Requests { get; } = [];

    public FakeHandler Respond(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((status, json));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.PathAndQuery, body, headers));
        var (status, json) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
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

    public Task<JiraUser> GetMyselfAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default) => throw new NotSupportedException();
}
