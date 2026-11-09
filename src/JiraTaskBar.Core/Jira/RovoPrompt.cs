namespace JiraTaskBar.Core.Jira;

public static class RovoPrompt
{
    public static string Build(JiraConnection connection, string issueKey, string question)
    {
        if (string.IsNullOrWhiteSpace(issueKey)) throw new ArgumentException("Select a ticket.", nameof(issueKey));
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Enter a question for Rovo.", nameof(question));
        return $"About Jira ticket {issueKey}:\n{connection.BrowseUrl(issueKey).AbsoluteUri}\n\n" +
            $"{question.Trim()}\n\nAnswer using information I can access, and link to relevant Jira items. " +
            "This is a read-only question; do not change tickets or take actions.";
    }
}
