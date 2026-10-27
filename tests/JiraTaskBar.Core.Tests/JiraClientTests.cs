using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.Core.Tests;

public class JiraClientTests
{
    private static (JiraClient Client, FakeHandler Handler) Create()
    {
        var handler = new FakeHandler();
        var client = new JiraClient(new HttpClient(handler),
            new JiraConnection(new Uri("https://acme.atlassian.net/"), "me@acme.com", "secret"));
        return (client, handler);
    }

    [Fact]
    public async Task Sends_basic_auth_header()
    {
        var (client, handler) = Create();
        handler.Respond("""{"accountId":"abc","displayName":"Pat"}""");

        var me = await client.GetMyselfAsync();

        Assert.Equal("Pat", me.DisplayName);
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me@acme.com:secret"));
        Assert.Equal(expected, handler.Requests[0].Headers["Authorization"]);
        Assert.Equal("/rest/api/3/myself", handler.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task Search_ranks_by_site_priority_order_then_key()
    {
        var (client, handler) = Create();
        handler.Respond("""[{"id":"1","name":"1 - Critical"},{"id":"2","name":"2 - High"},{"id":"3","name":"3 - Medium"}]""");
        handler.Respond("""
            {"issues":[
              {"key":"ABC-10","fields":{"summary":"ten","priority":{"id":"3","name":"3 - Medium"},"status":{"name":"Open"},"project":{"projectTypeKey":"software"},"issuetype":{"name":"Task"}}},
              {"key":"ABC-2","fields":{"summary":"two","priority":{"id":"3","name":"3 - Medium"},"status":{"name":"Open"},"project":{"projectTypeKey":"software"},"issuetype":{"name":"Task"}}},
              {"key":"SD-1","fields":{"summary":"crit","priority":{"id":"1","name":"1 - Critical"},"status":{"name":"Waiting"},"project":{"projectTypeKey":"service_desk"},"issuetype":{"name":"Incident"}}},
              {"key":"ABC-5","fields":{"summary":"none","priority":null,"status":{"name":"Open"},"project":{"projectTypeKey":"software"},"issuetype":{"name":"Task"}}}
            ],"isLast":true}
            """);

        var priorities = await client.GetPrioritiesAsync();
        var issues = await client.SearchAsync("assignee = currentUser()", priorities);

        Assert.Equal(["SD-1", "ABC-2", "ABC-10", "ABC-5"], issues.Select(i => i.Key));
        Assert.Equal(1, issues[0].PriorityRank);
        Assert.True(issues[0].IsServiceDesk);
        Assert.False(issues[1].IsServiceDesk);
        Assert.StartsWith("/rest/api/3/search/jql?jql=assignee%20%3D%20currentUser%28%29", handler.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task Search_follows_next_page_token()
    {
        var (client, handler) = Create();
        handler.Respond("""{"issues":[{"key":"A-1","fields":{"summary":"a"}}],"isLast":false,"nextPageToken":"tok 2"}""");
        handler.Respond("""{"issues":[{"key":"A-2","fields":{"summary":"b"}}],"isLast":true}""");

        var issues = await client.SearchAsync("project = A", []);

        Assert.Equal(2, issues.Count);
        Assert.Contains("nextPageToken=tok%202", handler.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task Worklog_payload_uses_jira_date_format()
    {
        var (client, handler) = Create();
        handler.Respond("{}", HttpStatusCode.Created);
        var started = new DateTimeOffset(2026, 10, 28, 9, 12, 5, TimeSpan.FromHours(13));

        await client.AddWorklogAsync("ABC-1", started, 3720);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/rest/api/3/issue/ABC-1/worklog", req.PathAndQuery);
        var body = JsonNode.Parse(req.Body!)!;
        Assert.Equal("2026-10-28T09:12:05.000+1300", body["started"]!.GetValue<string>());
        Assert.Equal(3720, body["timeSpentSeconds"]!.GetValue<int>());
        Assert.Null(body["comment"]);
    }

    [Fact]
    public void Negative_offsets_are_formatted()
    {
        var value = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5.5));
        Assert.Equal("2026-01-02T03:04:05.000-0530", JiraPayloads.FormatJiraDate(value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Service_desk_comment_sets_internal_property(bool isInternal)
    {
        var (client, handler) = Create();
        handler.Respond("{}", HttpStatusCode.Created);

        await client.AddCommentAsync("SD-1", "*bold*", isInternal);

        var req = handler.Requests.Single();
        Assert.Equal("/rest/api/2/issue/SD-1/comment", req.PathAndQuery);
        var body = JsonNode.Parse(req.Body!)!;
        Assert.Equal("*bold*", body["body"]!.GetValue<string>());
        var prop = body["properties"]![0]!;
        Assert.Equal("sd.public.comment", prop["key"]!.GetValue<string>());
        Assert.Equal(isInternal, prop["value"]!["internal"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Regular_comment_has_no_properties()
    {
        var (client, handler) = Create();
        handler.Respond("{}", HttpStatusCode.Created);

        await client.AddCommentAsync("ABC-1", "hello", null);

        Assert.Null(JsonNode.Parse(handler.Requests.Single().Body!)!["properties"]);
    }

    [Fact]
    public async Task Attachment_upload_sends_no_check_header()
    {
        using var dir = new TempDir();
        var file = dir.File("shot.png");
        File.WriteAllBytes(file, [1, 2, 3]);
        var (client, handler) = Create();
        handler.Respond("""[{"filename":"shot.png"}]""");

        var name = await client.AddAttachmentAsync("ABC-1", file);

        Assert.Equal("shot.png", name);
        var req = handler.Requests.Single();
        Assert.Equal("/rest/api/3/issue/ABC-1/attachments", req.PathAndQuery);
        Assert.Equal("no-check", req.Headers["X-Atlassian-Token"]);
    }

    [Fact]
    public async Task Errors_surface_jira_messages()
    {
        var (client, handler) = Create();
        handler.Respond("""{"errorMessages":["Issue does not exist"],"errors":{}}""", HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => client.AddCommentAsync("NOPE-1", "x", null));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("Issue does not exist", ex.Message);
    }

    [Theory]
    [InlineData("acme", "https://acme.atlassian.net/")]
    [InlineData("acme.atlassian.net", "https://acme.atlassian.net/")]
    [InlineData("https://acme.atlassian.net/jira/software/projects", "https://acme.atlassian.net/")]
    [InlineData(" https://jira.example.com/ ", "https://jira.example.com/")]
    public void Normalizes_site_url(string input, string expected)
    {
        Assert.Equal(expected, JiraConnection.NormalizeSiteUrl(input).ToString());
    }
}
