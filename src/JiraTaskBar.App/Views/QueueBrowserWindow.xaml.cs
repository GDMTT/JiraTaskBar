using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using JiraTaskBar.Core.Jira;
using Wpf.Ui.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGridRow = System.Windows.Controls.DataGridRow;

namespace JiraTaskBar.App.Views;

public partial class QueueBrowserWindow : FluentWindow
{
    internal const string TicketDragFormat = "JiraTaskBar.QueueBrowserTicket";
    private readonly Func<JiraIssue, int, string> _editPin;
    private readonly ObservableCollection<JiraQueue> _sources = [new("", "JQL search", "")];
    private readonly CancellationTokenSource _closed = new();
    private CancellationTokenSource? _load;
    private IReadOnlyList<JiraPriority>? _priorities;
    private ICollectionView? _view;
    private int _total;
    private Point _dragStart;
    private JiraIssue? _pressedTicket;

    internal QueueBrowserWindow(IJiraClient client, string jql, Func<JiraIssue, int, string> editPin)
    {
        Client = client;
        _editPin = editPin;
        InitializeComponent();
        JqlBox.Text = jql;
        SourceBox.ItemsSource = _sources;
        SourceBox.SelectedIndex = 0;
    }

    internal IJiraClient Client { get; }
    internal JiraIssue? DraggedTicket { get; private set; }
    internal void ReportPinStatus(string message) => PinStatus.Text = message;

    private async void Window_Loaded(object sender, RoutedEventArgs e) =>
        await Task.WhenAll(LoadQueuesAsync(), LoadTicketsAsync());

    private async Task LoadQueuesAsync()
    {
        SourceStatus.Text = "Looking for Jira Service Management queues…";
        try
        {
            var queues = await Client.GetQueuesAsync(_closed.Token);
            if (_closed.IsCancellationRequested) return;
            foreach (var queue in queues) _sources.Add(queue);
            SourceStatus.Text = queues.Count == 0 ? "No service queues are visible to this account. Use JQL search to browse tickets." : "";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SourceStatus.Text = $"Could not load service queues. JQL search is available. {ex.Message}";
        }
    }

    private async void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        JqlPanel.Visibility = SourceBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        await LoadTicketsAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await LoadTicketsAsync();

    private async void Jql_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoadTicketsAsync();
    }

    private async Task LoadTicketsAsync()
    {
        if (_closed.IsCancellationRequested || SourceBox.SelectedItem is not JiraQueue source) return;
        _load?.Cancel();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _load = load;
        _view = null;
        TicketGrid.ItemsSource = null;
        var query = SourceBox.SelectedIndex == 0 ? JqlBox.Text.Trim() : source.Jql;
        ResultsStatus.Text = "Loading Jira tickets…";
        PinStatus.Text = "";
        try
        {
            if (query.Length == 0)
            {
                ResultsStatus.Text = "Enter a Jira query to load tickets.";
                return;
            }
            _priorities ??= await Client.GetPrioritiesAsync(load.Token);
            var issues = await Client.BrowseIssuesAsync(query, _priorities, load.Token);
            if (load.IsCancellationRequested) return;
            var rows = issues.DistinctBy(issue => issue.Key, StringComparer.OrdinalIgnoreCase).Select(issue => new TicketRow(issue)).ToArray();
            SetChoices(ProjectBox, "All projects", rows.Select(row => row.Project));
            SetChoices(TypeBox, "All types", rows.Select(row => row.Issue.IssueType));
            SetChoices(StatusBox, "All statuses", rows.Select(row => row.Issue.Status));
            SetChoices(PriorityBox, "All priorities", rows.Select(row => row.Priority));
            _total = rows.Length;
            _view = CollectionViewSource.GetDefaultView(rows);
            _view.Filter = item => item is TicketRow row && Matches(row, Choice(ProjectBox), Choice(TypeBox),
                Choice(StatusBox), Choice(PriorityBox), TextFilter.Text.Trim());
            TicketGrid.ItemsSource = _view;
            RefreshFilters();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!load.IsCancellationRequested) ResultsStatus.Text = $"Could not load tickets. {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_load, load)) _load = null;
        }
    }

    private static void SetChoices(ComboBox box, string all, IEnumerable<string> values)
    {
        var selected = Choice(box);
        var choices = new[] { new FilterChoice(all, null) }.Concat(values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(value => new FilterChoice(value, value))).ToArray();
        box.ItemsSource = choices;
        box.SelectedItem = choices.FirstOrDefault(choice => choice.Value == selected) ?? choices[0];
    }

    private static string? Choice(ComboBox box) => (box.SelectedItem as FilterChoice)?.Value;

    internal static bool Matches(TicketRow row, string? project, string? type, string? status, string? priority, string text) =>
        InFilter(project, row.Project) && InFilter(type, row.Issue.IssueType) && InFilter(status, row.Issue.Status) &&
        InFilter(priority, row.Priority) && (row.Issue.Key.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            row.Issue.Summary.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static bool InFilter(string? filter, string value) =>
        filter is null || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshFilters();
    private void TextFilter_Changed(object sender, TextChangedEventArgs e) => RefreshFilters();

    private void RefreshFilters()
    {
        if (_view is null) return;
        _view.Refresh();
        ResultsStatus.Text = $"{_view.Cast<object>().Count()} of {_total} tickets";
    }

    private void Add_Click(object sender, RoutedEventArgs e) => EditSelectedPin(0);
    private void MoveUp_Click(object sender, RoutedEventArgs e) => EditSelectedPin(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => EditSelectedPin(1);

    private void EditSelectedPin(int direction) => ReportPinStatus(TicketGrid.SelectedItem is TicketRow row
        ? _editPin(row.Issue, direction) : "Select a ticket first.");

    private void Ticket_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(TicketGrid);
        var row = e.OriginalSource is DependencyObject element
            ? ItemsControl.ContainerFromElement(TicketGrid, element) as DataGridRow : null;
        _pressedTicket = (row?.Item as TicketRow)?.Issue;
    }

    private void Ticket_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedTicket is not { } ticket) return;
        var position = e.GetPosition(TicketGrid);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressedTicket = null;
        DraggedTicket = ticket;
        try { DragDrop.DoDragDrop(TicketGrid, new DataObject(TicketDragFormat, ticket.Key), DragDropEffects.Copy); }
        finally { DraggedTicket = null; }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed.Cancel();
        _closed.Dispose();
    }

    private sealed record FilterChoice(string Label, string? Value);
    internal sealed record TicketRow(JiraIssue Issue)
    {
        public string Project => Issue.Key.Split('-')[0];
        public string Priority => Issue.Priority?.Name ?? "No priority";
    }
}
