using JiraTaskBar.App.Services;
using JiraTaskBar.App.Views;
using JiraTaskBar.Core.Jira;
using System.Windows;
using System.Windows.Controls;

namespace JiraTaskBar.App.Tests;

public class QueueBrowserTests
{
    [Fact]
    public void DropUsesTheMidpointAlongTheBarOrientation()
    {
        var size = new Size(100, 30);
        Assert.False(BarWindow.IsAfterTicketMidpoint(new Point(20, 25), size, Orientation.Horizontal));
        Assert.True(BarWindow.IsAfterTicketMidpoint(new Point(20, 25), size, Orientation.Vertical));
    }

    [Fact]
    public void DropAndKeyboardActionsPreservePinOrderWithoutDuplicates()
    {
        List<string> pins = ["OPS-1", "OPS-3"];
        Assert.True(TicketPinOrder.Place(pins, "OPS-2", "OPS-3", after: false, moveExisting: true));
        Assert.False(TicketPinOrder.Place(pins, "ops-2", null, after: false, moveExisting: false));
        Assert.True(TicketPinOrder.Place(pins, "OPS-1", "OPS-3", after: true, moveExisting: true));
        Assert.True(TicketPinOrder.Move(pins, "OPS-1", -1));
        Assert.Equal(["OPS-2", "OPS-1", "OPS-3"], pins);
    }

    [Fact]
    public void QueueFiltersCombineMetadataAndSearchKeyOrSummary()
    {
        var row = new QueueBrowserWindow.TicketRow(new JiraIssue("OPS-7", "Restore access",
            new JiraPriority("1", "High", 1), "Open", "service_desk", "Request"));
        Assert.True(QueueBrowserWindow.Matches(row, "ops", "request", "open", "high", "restore"));
        Assert.True(QueueBrowserWindow.Matches(row, null, null, null, null, "ops-7"));
        Assert.False(QueueBrowserWindow.Matches(row, "ops", "request", "closed", "high", "restore"));
    }
}
