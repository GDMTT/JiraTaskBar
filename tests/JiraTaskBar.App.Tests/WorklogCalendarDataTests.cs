using JiraTaskBar.App.Services;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Tracking;
using System.Net.Http;

namespace JiraTaskBar.App.Tests;

public class WorklogCalendarDataTests
{
    private static readonly WorklogCalendarRange CalendarRange = new(new(2026, 11, 15), new(2026, 11, 22));
    private static JiraIssue Issue(string key) => new(key, key, null, "Open", "software", "Task");

    [Fact]
    public async Task Loads_worklogs_for_every_queue_ticket_and_selected_ticket_once()
    {
        var client = new CalendarClient();

        var result = await WorklogCalendarData.LoadAsync(client,
            [Issue("ABC-1"), Issue("ABC-2"), Issue("abc-1")], Issue("ABC-3"), CalendarRange, CancellationToken.None);

        Assert.Equal(["ABC-1", "ABC-2", "ABC-3"], client.RequestedKeys);
        Assert.Equal(["ABC-1", "ABC-2", "ABC-3"], result.Worklogs.Select(worklog => worklog.IssueKey));
        Assert.Equal(3, result.IssueKeys.Count);
        Assert.Equal(0, result.FailedIssueCount);
    }

    [Fact]
    public async Task Keeps_successful_ticket_worklogs_when_one_ticket_fails()
    {
        var client = new CalendarClient { FailingKey = "ABC-2" };

        var result = await WorklogCalendarData.LoadAsync(client,
            [Issue("ABC-1"), Issue("ABC-2")], Issue("ABC-1"), CalendarRange, CancellationToken.None);

        Assert.Equal("ABC-1", Assert.Single(result.Worklogs).IssueKey);
        Assert.Equal(1, result.FailedIssueCount);
    }

    [Fact]
    public async Task Discovers_tickets_outside_the_queue_and_only_counts_the_signed_in_account()
    {
        var client = new CalendarClient { Discovered = [Issue("DONE-42"), Issue("abc-1")] };

        var result = await WorklogCalendarData.LoadAsync(client, [Issue("ABC-1")], Issue("ABC-1"),
            CalendarRange, CancellationToken.None, ["done-42", "LOCAL-2"]);

        Assert.Equal(("my-account", new DateOnly(2026, 11, 24)), client.DiscoveryQuery);
        Assert.Equal(["ABC-1", "DONE-42", "LOCAL-2"], client.RequestedKeys);
        Assert.Equal(["ABC-1", "DONE-42", "LOCAL-2"], result.Worklogs.Select(worklog => worklog.IssueKey));
        Assert.Equal(1800, result.Worklogs.Sum(worklog => worklog.TimeSpentSeconds));
    }

    [Fact]
    public async Task Uses_known_tickets_and_reports_when_Jira_search_fails()
    {
        var client = new CalendarClient { FailDiscovery = true };

        var result = await WorklogCalendarData.LoadAsync(client, [], Issue("ABC-1"), CalendarRange, CancellationToken.None);

        Assert.True(result.DiscoveryFailed);
        Assert.Equal("ABC-1", Assert.Single(result.Worklogs).IssueKey);
    }

    [Fact]
    public void Replacing_one_tickets_history_keeps_other_ticket_worklogs()
    {
        var started = DateTimeOffset.Now;
        JiraWorklog[] existing =
        [
            new("1", "ABC-1", started, 600, "Me", null),
            new("2", "ABC-2", started, 900, "Me", null),
        ];
        JiraWorklog[] replacement = [new("3", "ABC-1", started, 1200, "Me", null)];

        var merged = WorklogCalendarData.ReplaceIssueWorklogs(existing, "abc-1", replacement);

        Assert.Equal(["ABC-2:2", "ABC-1:3"], merged.Select(worklog => $"{worklog.IssueKey}:{worklog.Id}"));
    }

    private sealed class CalendarClient : IJiraClient
    {
        public JiraConnection Connection { get; } = new(new Uri("https://example.atlassian.net/"), "me@example.com", "token");
        public List<string> RequestedKeys { get; } = [];
        public string? FailingKey { get; init; }
        public bool FailDiscovery { get; init; }
        public IReadOnlyList<JiraIssue> Discovered { get; init; } = [];
        public (string AccountId, DateOnly Until)? DiscoveryQuery { get; private set; }

        public Task<IReadOnlyList<JiraWorklog>> GetWorklogsAsync(string issueKey, CancellationToken ct = default)
        {
            RequestedKeys.Add(issueKey);
            if (issueKey.Equals(FailingKey, StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("temporary failure");
            IReadOnlyList<JiraWorklog> worklogs =
                [new(issueKey + "-worklog", issueKey, DateTimeOffset.Now, 600, "Me", null) { AuthorAccountId = "my-account" },
                 new(issueKey + "-other", issueKey, DateTimeOffset.Now, 1200, "Me", null) { AuthorAccountId = "another-account" }];
            return Task.FromResult(worklogs);
        }

        public Task<JiraUser> GetMyselfAsync(CancellationToken ct = default) => Task.FromResult(new JiraUser("my-account", "Me", null));
        public Task<IReadOnlyList<JiraIssue>> FindWorklogIssuesAsync(string accountId, DateOnly until, CancellationToken ct = default)
        {
            DiscoveryQuery = (accountId, until);
            return FailDiscovery ? throw new HttpRequestException("search unavailable") : Task.FromResult(Discovered);
        }
        public Task<JiraDurationSettings> GetTimeTrackingConfigurationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<JiraIssue> Issues, int FailedCount)> FetchIssuesByKeyAsync(IReadOnlyList<string> keys, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JiraSla?> GetRequestSlaAsync(string issueKey, CancellationToken ct = default) => Task.FromResult<JiraSla?>(null);
        public Task AddWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, string? comment = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateWorklogAsync(string issueKey, JiraWorklog worklog, DateTimeOffset startedLocal, int timeSpentSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddWorklogCommentAsync(string issueKey, string worklogId, string wikiMarkup, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> AddWorklogWithIdAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteWorklogAsync(string issueKey, string worklogId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> GetIssueTypeIconAsync(string avatarId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
