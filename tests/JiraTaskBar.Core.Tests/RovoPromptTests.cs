using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.Core.Tests;

public class RovoPromptTests
{
    [Fact]
    public void Prompt_preserves_ticket_and_question_without_credentials_or_gateway()
    {
        var connection = new JiraConnection(new Uri("https://acme.atlassian.net/"), "secret-email", "secret-token", Guid.NewGuid().ToString());
        var prompt = RovoPrompt.Build(connection, "ABC-12", "  What is blocking it?  ");
        Assert.Contains("ABC-12", prompt);
        Assert.Contains("https://acme.atlassian.net/browse/ABC-12", prompt);
        Assert.Contains("What is blocking it?", prompt);
        Assert.Contains("read-only", prompt);
        Assert.DoesNotContain("secret", prompt);
        Assert.DoesNotContain("api.atlassian.com", prompt);
    }
    [Fact]
    public void Blank_questions_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => RovoPrompt.Build(
            new JiraConnection(new Uri("https://acme.atlassian.net/"), "", ""), "A-1", " "));
    }
}
