using System.IO;
using System.Text;
using System.Windows;
using JiraTaskBar.App.Services;
using JiraTaskBar.App.Views;

namespace JiraTaskBar.App.Tests;

public class TicketInputTests
{
    private static readonly Uri CloudSite = new("https://example.atlassian.net/");
    private static readonly Uri DataCenterSite = new("https://jira.example.com:8443/team/jira/");

    [Theory]
    [InlineData("https://example.atlassian.net/browse/abc_2-123?focusedCommentId=1#comment", "ABC_2-123")]
    [InlineData("https://example.atlassian.net:443/browse/ABC-1", "ABC-1")]
    [InlineData("http://example.atlassian.net/browse/ABC-1", null)]
    [InlineData("https://else.atlassian.net/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net.evil.com/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net:8443/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net@evil.com/browse/ABC-1", null)]
    [InlineData("https://user@example.atlassian.net/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net/other/../browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net/browse/%41BC-1", null)]
    [InlineData("https://example.atlassian.net/browse/ABC-1%2Fmore", null)]
    [InlineData("https://example.atlassian.net/browse/ABC-1/extra", null)]
    [InlineData("https://example.atlassian.net/issues?url=/browse/ABC-1", null)]
    [InlineData("https://example.atlassian.net/browse/ABC-1\r\nhttps://example.atlassian.net/browse/ABC-2", null)]
    public void Parses_only_canonical_ticket_urls_on_the_connected_site(string url, string? expected) =>
        Assert.Equal(expected, TicketInput.KeyFromUrl(url, CloudSite));

    [Fact]
    public void Requires_the_configured_data_center_context_path_and_port()
    {
        Assert.Equal("TEAM-7", TicketInput.KeyFromUrl("https://jira.example.com:8443/team/jira/browse/TEAM-7", DataCenterSite));
        Assert.Null(TicketInput.KeyFromUrl("https://jira.example.com:8443/browse/TEAM-7", DataCenterSite));
        Assert.Null(TicketInput.KeyFromUrl("https://jira.example.com/team/jira/browse/TEAM-7", DataCenterSite));
    }

    [Fact]
    public void Accepts_native_browser_url_and_text_payloads()
    {
        const string url = "https://example.atlassian.net/browse/ABC-1";
        using var bytes = new MemoryStream(Encoding.Unicode.GetBytes(url + '\0'));
        var native = new DataObject();
        native.SetData("UniformResourceLocatorW", bytes, autoConvert: false);

        Assert.Equal("ABC-1", BarWindow.BrowserDropKey(native, CloudSite));
        Assert.Equal("ABC-1", BarWindow.BrowserDropKey(new DataObject(DataFormats.UnicodeText, url), CloudSite));
        Assert.Null(BarWindow.BrowserDropKey(new DataObject(DataFormats.Html, url), CloudSite));
        Assert.Null(BarWindow.BrowserDropKey(new DataObject(DataFormats.UnicodeText, url + "?" + new string('a', 2048)), CloudSite));
    }
}
