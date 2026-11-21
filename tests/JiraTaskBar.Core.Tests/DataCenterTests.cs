using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Storage;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.Core.Tests;

public class DataCenterTests
{
    private static readonly Uri Site = new("https://jira.example.org:8443/team/jira/");
    private const string Username = "agent";
    private const string Password = " example:password ";

    [Theory]
    [InlineData("http://jira.example.org/jira")]
    [InlineData("https://sample.atlassian.net/")]
    [InlineData("https://a.sample.atlassian.net./")]
    [InlineData("https://atlassian.net/")]
    [InlineData("https://api.atlassian.com./")]
    [InlineData("https://jira.example.org@another.example.org/")]
    [InlineData("https://jira.example.org/jira?password=sample")]
    [InlineData("https://jira.example.org/jira/%2e%2e")]
    public void DataCenterRejectsUnsafeAddresses(string url)
    {
        Assert.Throws<ArgumentException>(() => JiraConnection.NormalizeConnectionSite(url, true));
        Assert.Throws<ArgumentException>(() => new JiraClient(new HttpClient(new FakeHandler()),
            new JiraConnection(new Uri(url), Username, Password, IsDataCenter: true)));
    }

    [Fact]
    public async Task DataCenterSendsBasicCredentialsToTheConfiguredContext()
    {
        var handler = new FakeHandler().Respond("""
            {"name":"agent","key":"JIRAUSER10000","displayName":"Jordan"}
            """);
        var connection = new StoredCredentials("jira.example.org:8443/team/jira", Username, Password, IsDataCenter: true)
            .CreateConnection();
        var client = new JiraClient(new HttpClient(handler), connection);

        Assert.Equal(Site, client.Connection.SiteUrl);
        Assert.Equal(new Uri(Site, "browse/IT-1"), client.Connection.BrowseUrl("IT-1"));
        Assert.Equal("agent", (await client.GetMyselfAsync()).AccountId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(Site, "rest/api/2/myself"), request.Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")),
            request.Headers["Authorization"]);
        Assert.DoesNotContain(Password, connection.ToString());
    }

    [Fact]
    public async Task DataCenterSearchUsesOffsetPaginationAndJqlInTheRequestBody()
    {
        var handler = new FakeHandler()
            .Respond("""{"startAt":0,"total":2,"issues":[{"key":"IT-2","fields":{"summary":"Second"}}]}""")
            .Respond("""{"startAt":1,"total":2,"issues":[{"key":"IT-1","fields":{"summary":"First"}}]}""");
        var client = Create(handler);

        var issues = await client.FindWorklogIssuesAsync("agent", new DateOnly(2026, 11, 21));

        Assert.Equal(["IT-1", "IT-2"], issues.Select(issue => issue.Key));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(new Uri(Site, "rest/api/2/search"), request.Url);
        });
        var second = JsonNode.Parse(handler.Requests[1].Body!)!;
        Assert.Equal(1, second["startAt"]!.GetValue<int>());
        Assert.Equal("worklogAuthor = \"agent\" AND worklogDate < \"2026-11-21\"", second["jql"]!.GetValue<string>());
    }

    [Fact]
    public async Task DataCenterUsesWikiTextForWorklogsAndPreservesInternalCommentProperties()
    {
        var handler = new FakeHandler()
            .Respond("""
                {"startAt":0,"total":1,"worklogs":[{"id":"20","started":"2026-11-21T09:00:00.000+1300",
                "timeSpentSeconds":60,"author":{"name":"agent","displayName":"Jordan"},"comment":"*Original*"}]}
                """)
            .Respond("{}").Respond("{}").Respond("{}");
        var client = Create(handler);
        var worklog = Assert.Single(await client.GetWorklogsAsync("IT-1"));
        Assert.Equal("agent", worklog.AuthorAccountId);
        Assert.Equal("*Original*", worklog.Comment);
        await client.UpdateWorklogAsync("IT-1", worklog, worklog.Started, 120);
        await client.AddWorklogAsync("IT-1", worklog.Started, 120, "New");
        await client.AddWorklogCommentAsync("IT-1", worklog.Id, "Internal");

        Assert.Equal(new Uri(Site, "rest/api/2/issue/IT-1/worklog"), handler.Requests[0].Url);
        Assert.Equal(new Uri(Site, "rest/api/2/issue/IT-1/worklog/20"), handler.Requests[1].Url);
        Assert.Equal("*Original*", JsonNode.Parse(handler.Requests[1].Body!)!["comment"]!.GetValue<string>());
        Assert.Equal("New", JsonNode.Parse(handler.Requests[2].Body!)!["comment"]!.GetValue<string>());
        Assert.Equal(new Uri(Site, "rest/api/2/issue/IT-1/comment"), handler.Requests[3].Url);
        var properties = JsonNode.Parse(handler.Requests[3].Body!)!["properties"]!;
        Assert.True(properties[0]!["value"]!["internal"]!.GetValue<bool>());
        Assert.Equal("20", properties[1]!["value"]!["worklogId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.OK)]
    public async Task DataCenterSignInFailuresAreReportedWithoutEchoingSecrets(HttpStatusCode status)
    {
        var handler = new FakeHandler().Respond($$"""{"errorMessages":["{{Password}}"]}""", status);
        var error = await Assert.ThrowsAsync<JiraApiException>(() => Create(handler).GetMyselfAsync());
        Assert.Equal(401, error.StatusCode);
        Assert.DoesNotContain(Password, error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DataCenterBlockedBasicSignInDoesNotRetry()
    {
        var handler = new BlockedSignInHandler();
        var client = new JiraClient(new HttpClient(handler), new JiraConnection(Site, Username, Password, IsDataCenter: true),
            (_, _) => throw new InvalidOperationException("The sign-in request should not retry."));
        var error = await Assert.ThrowsAsync<JiraApiException>(() => client.GetMyselfAsync());
        Assert.Equal(401, error.StatusCode);
        Assert.Contains("CAPTCHA", error.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PendingBatchStopsOnSignInFailureAndKeepsUnsentWorklogs()
    {
        using var directory = new TempDir();
        var handler = new FakeHandler().Respond("{}").Respond("{}", HttpStatusCode.Unauthorized).Respond("{}").Respond("{}");
        var client = Create(handler);
        var store = new JsonFileStore<List<PendingWorklog>>(directory.File("queue.json"));
        var queue = new WorklogQueue(store);
        foreach (var key in new[] { "IT-1", "IT-2", "IT-3" })
            queue.Enqueue(new PendingWorklog(Guid.NewGuid(), key, DateTimeOffset.UnixEpoch, 60, null, 0));
        var service = new WorklogService(() => client, queue, () => ShortSessionMode.Skip);

        var error = await Assert.ThrowsAsync<JiraApiException>(() => service.RetryPendingAsync());
        Assert.Equal(401, error.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Collection(new WorklogQueue(store).Snapshot(),
            item => { Assert.Equal("IT-2", item.Key); Assert.Equal(1, item.Attempts); },
            item => { Assert.Equal("IT-3", item.Key); Assert.Equal(0, item.Attempts); });

        Assert.Equal(0, await service.RetryPendingAsync());
        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    public async Task DataCenterLoginPageKeepsTheWorklogPending(string contentType)
    {
        using var directory = new TempDir();
        var handler = new FakeHandler().RespondBytes("<html><title>Sign in</title></html>"u8.ToArray(), contentType);
        var store = new JsonFileStore<List<PendingWorklog>>(directory.File("queue.json"));
        var client = Create(handler);
        var service = new WorklogService(() => client, new WorklogQueue(store), () => ShortSessionMode.Skip);
        var start = DateTimeOffset.UnixEpoch;

        var result = await service.LogAsync(new CompletedSession("IT-1", start, start.AddMinutes(1)));
        Assert.Equal(WorklogOutcome.Queued, result.Outcome);
        Assert.True(result.IsAuthFailure);
        Assert.Equal("IT-1", Assert.Single(new WorklogQueue(store).Snapshot()).Key);
    }

    [Fact]
    public async Task DataCenterUsesTheConfiguredContextForConfigurationPriorityAttachmentsAndIcons()
    {
        using var directory = new TempDir();
        var path = directory.File("example.txt");
        File.WriteAllText(path, "example attachment");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var handler = new FakeHandler()
            .Respond("""{"timeTrackingConfiguration":{"workingHoursPerDay":7.5,"workingDaysPerWeek":5}}""")
            .Respond("""[{"id":"1","name":"Critical"}]""")
            .Respond("""[{"filename":"example.txt"}]""")
            .RespondBytes(png, "image/png");
        var client = Create(handler);
        Assert.Equal(7.5m, (await client.GetTimeTrackingConfigurationAsync()).HoursPerDay);
        Assert.Equal("Critical", Assert.Single(await client.GetPrioritiesAsync()).Name);
        Assert.Equal("example.txt", await client.AddAttachmentAsync("IT-1", path));
        Assert.Equal(png, await client.GetIssueTypeIconAsync("10100"));
        Assert.Equal(new Uri(Site, "rest/api/2/configuration"), handler.Requests[0].Url);
        Assert.Equal(new Uri(Site, "rest/api/2/priority"), handler.Requests[1].Url);
        Assert.Equal(new Uri(Site, "rest/api/2/issue/IT-1/attachments"), handler.Requests[2].Url);
        Assert.Equal("no-check", handler.Requests[2].Headers["X-Atlassian-Token"]);
        Assert.Equal(new Uri(Site, "secure/viewavatar?size=medium&avatarId=10100&avatarType=issuetype"), handler.Requests[3].Url);
    }

    private static JiraClient Create(FakeHandler handler) => new(new HttpClient(handler),
        new JiraConnection(Site, Username, Password, IsDataCenter: true));

    private sealed class BlockedSignInHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-Seraph-LoginReason", "AUTHENTICATION_DENIED");
            return Task.FromResult(response);
        }
    }
}
