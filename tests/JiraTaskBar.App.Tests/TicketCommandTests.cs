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
        Assert.False(ticket.CalendarCommand.CanExecute(null));
        Assert.False(ticket.AskRovoCommand.CanExecute(null));
        Assert.False(invoked);

        connected = true;
        ticket.RefreshConnectionState();

        Assert.True(ticket.CommentCommand.CanExecute(null));
        Assert.True(ticket.CalendarCommand.CanExecute(null));
        Assert.True(ticket.AskRovoCommand.CanExecute(null));
        ticket.CommentCommand.Execute(null);
        Assert.True(invoked);
    }

    [Fact]
    public void Rovo_command_keeps_selected_ticket_context_and_does_not_toggle_timer()
    {
        var toggled = false;
        TicketViewModel? selected = null;
        var ticket = new TicketViewModel(Issue, _ => toggled = true, _ => { }, _ => { }, askRovo: t => selected = t);
        ticket.AskRovoCommand.Execute(null);
        Assert.Same(ticket, selected);
        Assert.False(toggled);
    }
}
