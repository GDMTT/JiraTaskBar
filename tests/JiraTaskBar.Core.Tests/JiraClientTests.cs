using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.Core.Tests;

public class JiraClientTests
{
    [Fact]
    public async Task QueueCatalogPagesProjectsAndQueuesOnTheConnectedSite()
    {
        var (client, handler) = Create();
        handler.Respond("""{"start":0,"isLastPage":false,"values":[{"id":"1","projectKey":"OPS"}]}""")
            .Respond("""{"start":1,"isLastPage":true,"values":[{"id":"2","projectKey":"HELP"}]}""")
            .Respond("""{"start":0,"isLastPage":false,"values":[{"name":"Waiting","jql":"status = Waiting"}]}""")
            .Respond("""{"start":1,"isLastPage":true,"values":[{"name":"New","jql":"status = New"}]}""")
            .Respond("{}", HttpStatusCode.Forbidden);

        var queues = await client.GetQueuesAsync();

        Assert.Collection(queues,
            queue => Assert.Equal(new JiraQueue("OPS", "New", "status = New"), queue),
            queue => Assert.Equal(new JiraQueue("OPS", "Waiting", "status = Waiting"), queue));
        Assert.EndsWith("/servicedesk?start=1&limit=50", handler.Requests[1].PathAndQuery);
        Assert.EndsWith("/servicedesk/1/queue?start=1&limit=50", handler.Requests[3].PathAndQuery);
    }

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
    public async Task Reads_site_duration_conventions()
    {
        var (client, handler) = Create();
        handler.Respond("""{"timeTrackingConfiguration":{"workingHoursPerDay":7.5,"workingDaysPerWeek":4}}""");

        var settings = await client.GetTimeTrackingConfigurationAsync();

        Assert.Equal(7.5m, settings.HoursPerDay);
        Assert.Equal(4m, settings.DaysPerWeek);
        Assert.Equal("/rest/api/3/configuration", handler.Requests.Single().PathAndQuery);
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
    public async Task Added_ticket_lookup_associates_a_moved_issue_with_its_requested_key()
    {
        var (client, handler) = Create();
        handler.Respond("""{"key":"ABC-2","fields":{"summary":"Outside the queue","priority":{"id":"4"}}}""");

        var result = Assert.Single(await client.FetchIssuesByKeyAsync(["OLD-2", "old-2"],
            [new JiraPriority("4", "Medium", 2)]));

        Assert.Equal("OLD-2", result.RequestedKey);
        Assert.Equal("ABC-2", result.Issue!.Key);
        Assert.Equal(2, result.Issue.PriorityRank);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.StartsWith("/rest/api/3/issue/OLD-2?fields=", request.PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    public async Task Added_ticket_lookup_distinguishes_missing_issues_from_retryable_failures(HttpStatusCode status, bool canRetry)
    {
        var (client, handler) = Create();
        handler.Respond("{}", status);

        Assert.Equal(new JiraIssueLookup("ABC-2", null, canRetry),
            Assert.Single(await client.FetchIssuesByKeyAsync(["ABC-2"], [])));
    }

    [Fact]
    public async Task Worklog_issue_search_includes_older_starts_and_reads_past_the_queue_limit()
    {
        var (client, handler) = Create();
        for (var page = 0; page < 3; page++)
        {
            handler.Respond(System.Text.Json.JsonSerializer.Serialize(new
            {
                issues = Enumerable.Range(page * 100 + 1, 100).Select(id => new { key = $"TEST-{id}" }),
                isLast = page == 2,
                nextPageToken = $"page {page + 1}",
            }));
        }

        var issues = await client.FindWorklogIssuesAsync("site:account", new(2026, 11, 24));

        Assert.Equal(300, issues.Count);
        Assert.Contains("worklogAuthor = \"site:account\" AND worklogDate < \"2026-11-24\"&",
            Uri.UnescapeDataString(handler.Requests[0].PathAndQuery));
        Assert.Contains("nextPageToken=page%202", handler.Requests[2].PathAndQuery);
    }

    [Fact]
    public async Task Worklog_preview_sends_date_bounds_to_jira()
    {
        var (client, handler) = Create();
        handler.Respond("""{"issues":[],"isLast":true}""");
        handler.Respond("""{"total":0,"worklogs":[]}""");
        var from = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.FromHours(13));
        var until = from.AddDays(8);

        await client.FindWorklogIssuesAsync("site:account", new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 24));
        await client.GetWorklogsAsync("ABC-1", from, until);

        var jql = Uri.UnescapeDataString(handler.Requests[0].PathAndQuery);
        Assert.Contains("worklogDate >= \"2026-11-12\"", jql);
        Assert.Contains("worklogDate < \"2026-11-24\"", jql);
        Assert.EndsWith($"&startedAfter={from.ToUnixTimeMilliseconds() - 1}&startedBefore={until.ToUnixTimeMilliseconds()}",
            handler.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task Request_sla_keeps_the_latest_completed_cycle_ahead_of_the_due_date()
    {
        var (client, handler) = Create();
        handler.Respond("""
            {"values":[
                {"name":"Time to first response","completedCycles":[{"breached":true}]},
                {"name":"Time to resolution","completedCycles":[]}
            ],"isLastPage":false}
            """);
        handler.Respond("""
            {"values":[{"name":"Time to resolution","completedCycles":[
                {"stopTime":{"epochMillis":2000},"breached":false},
                {"stopTime":{"epochMillis":1000},"breached":true}
            ]}],"isLastPage":true}
            """);

        var sla = await client.GetRequestSlaAsync("SD-2");
        Assert.Equal("/rest/servicedeskapi/request/SD-2/sla?start=0&limit=50", handler.Requests[0].PathAndQuery);
        Assert.Contains("start=2", handler.Requests[1].PathAndQuery);
        var issue = new JiraIssue("SD-2", "Ticket", null, "Done", "service_desk", "Task",
            DueDate: new DateOnly(2026, 11, 1)) { Sla = sla };
        var now = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        var urgency = DueDateProgress.CalculateUrgency(issue, now, TimeZoneInfo.Utc);
        Assert.Equal(DueDateKind.Met, urgency.Kind);
        Assert.Equal("Time to resolution (SLA): Met", urgency.Tooltip);
        var breached = issue with { Sla = Assert.IsType<JiraSla>(sla) with { Breached = true } };
        Assert.Equal("Time to resolution (SLA): Breached",
            DueDateProgress.CalculateUrgency(breached, now, TimeZoneInfo.Utc).Tooltip);
        Assert.Equal(DueDateKind.Overdue, DueDateProgress.CalculateUrgency(issue with { Sla = null }, now, TimeZoneInfo.Utc).Kind);
        Assert.Equal(DueDateKind.None,
            DueDateProgress.CalculateUrgency(issue with { Sla = null, DueDate = null }, now, TimeZoneInfo.Utc).Kind);
    }

    [Fact]
    public async Task Request_sla_does_not_use_other_metrics_when_resolution_is_missing()
    {
        var (client, handler) = Create();
        handler.Respond("""
            {"values":[
                {"name":"Time to first response","completedCycles":[{"breached":true}]},
                {"name":"Time to first resolution","ongoingCycle":{"breached":false}}
            ],"isLastPage":true}
            """);

        Assert.Null(await client.GetRequestSlaAsync("SD-2"));
    }

    [Fact]
    public async Task Ongoing_sla_supersedes_past_cycles_and_preserves_paused_state()
    {
        var (client, handler) = Create();
        handler.Respond("""
            {"values":[{"name":"Time To Resolution","ongoingCycle":{"breached":false,"withinCalendarHours":false,
                "goalDuration":{"millis":600000},"elapsedTime":{"millis":300000},"breachTime":{"epochMillis":2000}},
                "completedCycles":[{"breached":true}]}],"isLastPage":true}
            """);
        var issue = new JiraIssue("SD-2", "Ticket", null, "Open", "service_desk", "Task")
            { Sla = await client.GetRequestSlaAsync("SD-2") };
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(2000), issue.Sla!.BreachAt);
        var urgency = DueDateProgress.CalculateUrgency(issue, DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        Assert.Contains("Paused", urgency.Tooltip);
        Assert.Contains("Remaining when checked at", urgency.Tooltip);
        Assert.Equal(0.5, urgency.RemainingFraction);
        var stale = DueDateProgress.CalculateUrgency(issue, DateTimeOffset.UtcNow.AddMinutes(11), TimeZoneInfo.Utc);
        Assert.Contains("Waiting for a fresh Jira reading", stale.Tooltip);
        Assert.Null(stale.RemainingFraction);
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
    public async Task Worklog_description_is_sent_as_plain_text_adf()
    {
        var (client, handler) = Create();
        handler.Respond("{}", HttpStatusCode.Created);

        await client.AddWorklogAsync("ABC-1", DateTimeOffset.Now, 60, "first line\nsecond line");

        var comment = JsonNode.Parse(handler.Requests.Single().Body!)!["comment"]!;
        Assert.Equal("doc", comment["type"]!.GetValue<string>());
        Assert.Equal(2, comment["content"]!.AsArray().Count);
    }

    [Fact]
    public async Task Existing_worklog_can_be_moved_and_resized()
    {
        var (client, handler) = Create();
        handler.Respond("{}", HttpStatusCode.OK);
        var moved = new DateTimeOffset(2026, 11, 6, 13, 30, 0, TimeSpan.FromHours(13));

        var original = new JiraWorklog("12345", "ABC-1", DateTimeOffset.Now, 60, "Pat", "updated",
            JsonNode.Parse("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"up"},{"type":"text","text":"dated","marks":[{"type":"strong"}]}]}]}"""));

        await client.UpdateWorklogAsync("ABC-1", original, moved, 5400);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/rest/api/3/issue/ABC-1/worklog/12345", request.PathAndQuery);
        var body = JsonNode.Parse(request.Body!)!;
        Assert.Equal(5400, body["timeSpentSeconds"]!.GetValue<int>());
        Assert.Equal("dated", body["comment"]!["content"]![0]!["content"]![1]!["text"]!.GetValue<string>());
        Assert.Equal("strong", body["comment"]!["content"]![0]!["content"]![1]!["marks"]![0]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("../1")]
    [InlineData("abc")]
    public async Task Worklog_update_rejects_unsafe_ids(string id)
    {
        var (client, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UpdateWorklogAsync("ABC-1",
                new JiraWorklog(id, "ABC-1", DateTimeOffset.Now, 60, "Pat", null), DateTimeOffset.Now, 60));
    }

    [Fact]
    public async Task Worklog_history_is_parsed_and_paginated()
    {
        var (client, handler) = Create();
        handler.Respond("""
            {"startAt":0,"maxResults":1,"total":2,"worklogs":[
              {"id":"10","started":"2026-11-03T09:00:00.000+1300","timeSpentSeconds":900,
               "author":{"displayName":"Pat","accountId":"atlassian-account"},"comment":{"type":"doc","version":1,"content":[
                 {"type":"paragraph","content":[{"type":"text","text":"Investigated issue"}]}]}}
            ]}
            """);
        handler.Respond("""
            {"startAt":1,"maxResults":1,"total":2,"worklogs":[
              {"id":"11","started":"2026-11-03T10:00:00.000+1300","timeSpentSeconds":1800,
               "author":{"displayName":"Pat"}}
            ]}
            """);

        var worklogs = await client.GetWorklogsAsync("ABC-1");

        Assert.Equal(2, worklogs.Count);
        Assert.Equal("Investigated issue", worklogs[0].Comment);
        Assert.Equal(900, worklogs[0].TimeSpentSeconds);
        Assert.Equal("Pat", worklogs[0].Author);
        Assert.Equal("atlassian-account", worklogs[0].AuthorAccountId);
        Assert.Equal("/rest/api/3/issue/ABC-1/worklog?startAt=0&maxResults=100", handler.Requests[0].PathAndQuery);
        Assert.Contains("startAt=1", handler.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task Worklog_pagination_uses_raw_page_count_when_an_entry_is_invalid()
    {
        var (client, handler) = Create();
        handler.Respond("""
            {"startAt":0,"total":3,"worklogs":[
              {"id":"bad","started":"not-a-date","timeSpentSeconds":10},
              {"id":"10","started":"2026-11-03T09:00:00Z","timeSpentSeconds":900}
            ]}
            """);
        handler.Respond("""
            {"startAt":2,"total":3,"worklogs":[
              {"id":"11","started":"2026-11-03T10:00:00Z","timeSpentSeconds":1800}
            ]}
            """);

        var worklogs = await client.GetWorklogsAsync("ABC-1");

        Assert.Equal(["10", "11"], worklogs.Select(w => w.Id));
        Assert.Contains("startAt=2", handler.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task Worklog_history_is_not_silently_truncated_at_one_thousand()
    {
        var (client, handler) = Create();
        const int total = 1001;
        for (var start = 0; start < total; start += 100)
        {
            var worklogs = new JsonArray();
            foreach (var id in Enumerable.Range(start, Math.Min(100, total - start)))
                worklogs.Add(new JsonObject
                {
                    ["id"] = id.ToString(),
                    ["started"] = "2026-11-03T09:00:00Z",
                    ["timeSpentSeconds"] = 60,
                });
            handler.Respond(new JsonObject
            {
                ["startAt"] = start,
                ["total"] = total,
                ["worklogs"] = worklogs,
            }.ToJsonString());
        }

        var result = await client.GetWorklogsAsync("ABC-1");

        Assert.Equal(total, result.Count);
        Assert.Equal(11, handler.Requests.Count);
    }

    [Fact]
    public async Task Adf_worklog_comments_preserve_inline_text_and_separate_blocks()
    {
        var (client, handler) = Create();
        handler.Respond("""
            {"total":1,"worklogs":[{
              "id":"10","started":"2026-11-03T09:00:00Z","timeSpentSeconds":60,
              "comment":{"type":"doc","content":[
                {"type":"paragraph","content":[
                  {"type":"text","text":"--"},{"type":"text","text":"help"},
                  {"type":"hardBreak"},{"type":"text","text":"next"}]},
                {"type":"paragraph","content":[{"type":"text","text":"line"}]}
              ]}
            }]}
            """);

        var worklog = Assert.Single(await client.GetWorklogsAsync("ABC-1"));

        Assert.Equal("--help\nnext\nline", worklog.Comment?.Replace("\r\n", "\n"));
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
    public void Comment_payload_always_carries_the_visibility_flag()
    {
        // A JSM comment without sd.public.comment is public, so the flag must never be omitted.
        var body = JiraPayloads.Comment("hello", isInternal: true);

        var prop = Assert.Single(body["properties"]!.AsArray())!;
        Assert.Equal("sd.public.comment", prop["key"]!.GetValue<string>());
        Assert.True(prop["value"]!["internal"]!.GetValue<bool>());
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

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => client.AddCommentAsync("NOPE-1", "x", true));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("Issue does not exist", ex.Message);
    }

    [Theory]
    [InlineData("acme", "https://acme.atlassian.net/")]
    [InlineData("acme.atlassian.net", "https://acme.atlassian.net/")]
    [InlineData(" ACME.Atlassian.net/ ", "https://acme.atlassian.net/")]
    [InlineData("https://acme.atlassian.net/jira/software/projects?x=1", "https://acme.atlassian.net/")]
    public void Normalizes_site_url(string input, string expected)
    {
        Assert.Equal(expected, JiraConnection.NormalizeSiteUrl(input).ToString());
    }

    [Theory]
    [InlineData("http://acme.atlassian.net")]              // cleartext would expose the token
    [InlineData("https://jira.example.com")]               // not Jira Cloud
    [InlineData("https://acme.atlassian.net.evil.test")]   // look-alike suffix
    [InlineData("https://evilatlassian.net")]              // no subdomain boundary
    [InlineData("https://a.b.atlassian.net")]              // nested subdomain
    [InlineData("https://acme.atlassian.net:8443")]        // non-default port
    [InlineData("https://user:pw@acme.atlassian.net")]     // embedded credentials
    [InlineData("ftp://acme.atlassian.net")]
    [InlineData("")]
    public void Rejects_unsafe_site_urls(string input)
    {
        Assert.Throws<ArgumentException>(() => JiraConnection.NormalizeSiteUrl(input));
    }

    [Fact]
    public void Client_refuses_non_https_connection()
    {
        Assert.Throws<ArgumentException>(() => new JiraClient(new HttpClient(new FakeHandler()),
            new JiraConnection(new Uri("http://acme.atlassian.net/"), "me@acme.com", "secret")));
    }

    [Fact]
    public async Task Deletes_one_worklog_without_changing_the_remaining_estimate()
    {
        var (client, handler) = Create();
        handler.Respond("""{"startAt":0,"total":1,"comments":[{"id":"10","body":"Older unlinked comment"}]}""");
        handler.Respond(string.Empty, HttpStatusCode.NoContent);

        await client.DeleteWorklogAsync("SD-1", "42");

        var request = Assert.Single(handler.Requests, item => item.Method == HttpMethod.Delete);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/rest/api/3/issue/SD-1/worklog/42?adjustEstimate=leave", request.PathAndQuery);
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteWorklogAsync("SD-1", "42/43"));
    }

    [Fact]
    public async Task Creates_the_worklog_comment_with_its_exact_id_and_internal_visibility()
    {
        var (client, handler) = Create();
        handler.Respond("""{"id":"42"}""", HttpStatusCode.Created);
        handler.Respond("""{"id":"11"}""", HttpStatusCode.Created);

        var worklogId = await client.AddWorklogWithIdAsync("SD-1", DateTimeOffset.UtcNow, 60);
        Assert.Equal("42", worklogId);
        await client.AddWorklogCommentAsync("SD-1", worklogId!, "Progress");

        var request = handler.Requests[1];
        var properties = JsonNode.Parse(request.Body!)!["properties"]!.AsArray();
        Assert.Equal("/rest/api/2/issue/SD-1/comment", request.PathAndQuery);
        Assert.Contains(properties, property => property!["key"]!.GetValue<string>() == "sd.public.comment" &&
            property["value"]!["internal"]!.GetValue<bool>());
        Assert.Contains(properties, property => property!["key"]!.GetValue<string>() == "jirataskbar.worklog" &&
            property["value"]!["worklogId"]!.GetValue<string>() == worklogId);
    }

    [Fact]
    public async Task Delete_only_removes_comments_explicitly_linked_to_that_worklog_across_pages()
    {
        var (client, handler) = Create();
        handler.Respond("""
            {"startAt":0,"total":3,"comments":[
              {"id":"10","body":"Same text","properties":[{"key":"sd.public.comment","value":{"internal":true}}]},
              {"id":"11","body":"Same text","properties":[{"key":"jirataskbar.worklog","value":{"worklogId":"99"}}]}
            ]}
            """);
        handler.Respond("""
            {"startAt":2,"total":3,"comments":[
              {"id":"12","body":"Same text","properties":[{"key":"jirataskbar.worklog","value":{"worklogId":"42"}}]}
            ]}
            """);
        handler.Respond(string.Empty, HttpStatusCode.NoContent);
        handler.Respond(string.Empty, HttpStatusCode.NoContent);

        await client.DeleteWorklogAsync("SD-1", "42");

        Assert.Contains("expand=properties", handler.Requests[0].PathAndQuery);
        Assert.Contains("startAt=2", handler.Requests[1].PathAndQuery);
        Assert.Equal(
            ["/rest/api/2/issue/SD-1/comment/12", "/rest/api/3/issue/SD-1/worklog/42?adjustEstimate=leave"],
            handler.Requests.Where(item => item.Method == HttpMethod.Delete).Select(item => item.PathAndQuery));
    }

    [Fact]
    public async Task Failed_linked_comment_deletion_keeps_the_worklog_available_for_retry()
    {
        var (client, handler) = Create();
        handler.Respond("""
            {"startAt":0,"total":2,"comments":[
              {"id":"11","properties":[{"key":"jirataskbar.worklog","value":{"worklogId":"42"}}]},
              {"id":"12","properties":[{"key":"jirataskbar.worklog","value":{"worklogId":"42"}}]}
            ]}
            """);
        handler.Respond(string.Empty, HttpStatusCode.NoContent);
        handler.Respond("""{"errorMessages":["Missing permission"]}""", HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteWorklogAsync("SD-1", "42"));
        Assert.Contains("Jira deleted 1 linked comment", error.Message);
        Assert.DoesNotContain(handler.Requests, item => item.Method == HttpMethod.Delete && item.PathAndQuery.Contains("/worklog/"));

        handler.Respond("""
            {"startAt":0,"total":1,"comments":[
              {"id":"12","properties":[{"key":"jirataskbar.worklog","value":{"worklogId":"42"}}]}
            ]}
            """);
        handler.Respond(string.Empty, HttpStatusCode.NoContent);
        handler.Respond(string.Empty, HttpStatusCode.NoContent);

        await client.DeleteWorklogAsync("SD-1", "42");

        Assert.Equal("/rest/api/2/issue/SD-1/comment/12", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/rest/api/3/issue/SD-1/worklog/42?adjustEstimate=leave", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Delete_stops_before_any_changes_when_comment_pagination_is_inconsistent()
    {
        var (client, handler) = Create();
        handler.Respond("""
            {"startAt":0,"total":2,"comments":[
              {"id":"12","properties":[{"key":"jirataskbar.worklog","value":{"worklogId":"42"}}]}
            ]}
            """);
        handler.Respond("""{"startAt":1,"total":1,"comments":[]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteWorklogAsync("SD-1", "42"));

        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task Redirects_are_errors_not_followed()
    {
        var (client, handler) = Create();
        handler.Respond("", HttpStatusCode.Found);

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => client.GetMyselfAsync());

        Assert.Equal(302, ex.StatusCode);
        Assert.Single(handler.Requests);
    }
}
