using System.Net;
using System.Text.Json.Nodes;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.Core.Tests;

public class IssueTypeIconTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly Uri Site = new("https://acme.atlassian.net/");

    [Theory]
    [InlineData("""{"name":"Task","avatarId":10318}""", "10318")]
    [InlineData("""{"name":"Task","iconUrl":"https://acme.atlassian.net/rest/api/2/universal_avatar/view/type/issuetype/avatar/10307?size=medium"}""", "10307")]
    [InlineData("""{"name":"Bug","iconUrl":"https://acme.atlassian.net/images/icons/issuetypes/bug.png"}""", null)]
    [InlineData("""{"name":"Odd","avatarId":"../../x"}""", null)]
    [InlineData("""{"name":"None"}""", null)]
    public void Avatar_id_comes_from_the_issue_type(string issueTypeJson, string? expected)
    {
        Assert.Equal(expected, JiraPayloads.AvatarId(JsonNode.Parse(issueTypeJson)));
    }

    [Fact]
    public void Search_results_carry_the_avatar_id()
    {
        var node = JsonNode.Parse("""
            {"issues":[{"key":"SD-1","fields":{"summary":"x","issuetype":{"name":"Incident","avatarId":10304}}}],"isLast":true}
            """);
        var (issues, _) = JiraPayloads.ParseSearch(node, new Dictionary<string, JiraPriority>());
        Assert.Equal("10304", issues.Single().IssueTypeAvatarId);
    }

    [Theory]
    [InlineData(null, "acme.atlassian.net", "/rest/api/3/universal_avatar/view/type/issuetype/avatar/10318")]
    [InlineData("1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d", "api.atlassian.com",
        "/ex/jira/1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d/rest/api/3/universal_avatar/view/type/issuetype/avatar/10318")]
    public async Task Icon_is_requested_as_png_from_the_api_base(string? cloudId, string host, string path)
    {
        var handler = new FakeHandler().RespondBytes(Png, "image/png");
        var client = new JiraClient(new HttpClient(handler), new JiraConnection(Site, "me@acme.com", "t", cloudId));

        var bytes = await client.GetIssueTypeIconAsync("10318");

        Assert.Equal(Png, bytes);
        var url = handler.Requests.Single().Url;
        Assert.Equal(host, url.Host);
        Assert.Equal(path, url.AbsolutePath);
        Assert.Contains("format=png", url.Query);
    }

    [Fact]
    public async Task Non_png_icons_are_ignored()
    {
        var handler = new FakeHandler().RespondBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), "image/svg+xml");
        var client = new JiraClient(new HttpClient(handler), new JiraConnection(Site, "me@acme.com", "t"));

        Assert.Null(await client.GetIssueTypeIconAsync("10318"));
    }

    [Fact]
    public async Task Icon_errors_surface_as_jira_errors()
    {
        var handler = new FakeHandler().Respond("{}", HttpStatusCode.Forbidden);
        var client = new JiraClient(new HttpClient(handler), new JiraConnection(Site, "me@acme.com", "t"));

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => client.GetIssueTypeIconAsync("10318"));
        Assert.Equal(403, ex.StatusCode);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("10318?x=1")]
    [InlineData("")]
    public async Task Avatar_id_must_be_numeric(string avatarId)
    {
        var handler = new FakeHandler();
        var client = new JiraClient(new HttpClient(handler), new JiraConnection(Site, "me@acme.com", "t"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetIssueTypeIconAsync(avatarId));
        Assert.Empty(handler.Requests);
    }
}
