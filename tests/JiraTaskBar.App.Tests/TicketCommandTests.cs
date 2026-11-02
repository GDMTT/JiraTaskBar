using JiraTaskBar.App.ViewModels;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.Tests;

public class TicketCommandTests
{
    private static readonly JiraIssue Issue = new(
        "ABC-1", "Test", null, "Open", "software", "Task");

    [Fact]
    public void Comment_command_tracks_connection_availability()
    {
        var connected = false;
        var invoked = false;
        var ticket = new TicketViewModel(Issue, _ => { }, _ => { }, _ => invoked = true,
            canComment: () => connected);

        Assert.False(ticket.CommentCommand.CanExecute(null));
        Assert.False(invoked);

        connected = true;
        ticket.RefreshConnectionState();

        Assert.True(ticket.CommentCommand.CanExecute(null));
        ticket.CommentCommand.Execute(null);
        Assert.True(invoked);
    }
}
