using JiraTaskBar.App.Services;

namespace JiraTaskBar.App.Tests;

public class ShellTests
{
    [Theory]
    [InlineData("https://acme.atlassian.net/browse/ABC-1")]
    [InlineData("http://example.com/page")]
    [InlineData("mailto:someone@example.com")]
    public void Allows_web_and_mail_links(string url) => Assert.True(Shell.IsSafeUrl(url));

    [Theory]
    [InlineData(@"file://attacker/share/payload.exe")]
    [InlineData(@"file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("search-ms:query=x&crumb=location:\\\\attacker\\share")]
    [InlineData("javascript://%0aalert(1)")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_anything_that_could_launch_a_program(string? url) => Assert.False(Shell.IsSafeUrl(url));
}
