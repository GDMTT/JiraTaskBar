using System.Windows;
using JiraTaskBar.App.ViewModels;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.App.Views;

public partial class WorklogCalendarWindow
{
    private readonly JiraIssue _issue;
    private readonly IJiraClient _client;
    private readonly Func<IReadOnlyList<PendingWorklog>> _pending;
    private readonly CancellationTokenSource _closed = new();
    private CancellationTokenSource? _load;
    private int _loadVersion;
    private bool _isClosed;
    private IReadOnlyList<JiraWorklog> _worklogs = [];

    public WorklogCalendarWindow(JiraIssue issue, IJiraClient client, Func<IReadOnlyList<PendingWorklog>> pending)
    {
        InitializeComponent();
        _issue = issue;
        _client = client;
        _pending = pending;
        WindowTitle.Title = $"Time calendar – {issue.Key}";
        TicketTitle.Text = $"{issue.Key}  {issue.Summary}";
        DateCalendar.SelectedDate = DateTime.Today;
        DateCalendar.DisplayDate = DateTime.Today;
    }

    public string IssueKey => _issue.Key;

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    public async Task RefreshAsync()
    {
        if (_isClosed) return;
        var version = ++_loadVersion;
        _load?.Cancel();
        var load = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _load = load;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Loading Jira worklogs…";
        try
        {
            var worklogs = await _client.GetWorklogsAsync(_issue.Key, load.Token);
            if (version != _loadVersion) return;
            _worklogs = worklogs;
            StatusText.Text = $"{_worklogs.Count} Jira worklog(s) loaded";
            RefreshSelectedDay();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
                StatusText.Text = $"Could not load worklogs: {ex.Message}";
        }
        finally
        {
            if (version == _loadVersion)
            {
                _load = null;
                RefreshButton.IsEnabled = true;
            }
            load.Dispose();
        }
    }

    private void Calendar_SelectedDatesChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshSelectedDay();

    private void RefreshSelectedDay()
    {
        if (DateCalendar.SelectedDate is not { } selected) return;
        var date = DateOnly.FromDateTime(selected);
        var rows = _worklogs
            .Where(w => DateOnly.FromDateTime(w.Started.ToLocalTime().DateTime) == date)
            .Select(w => new WorklogRow(w.Started.ToLocalTime(), w.TimeSpentSeconds, w.Author, "Saved", w.Comment))
            .Concat(_pending()
                .Where(p => p.Key.Equals(_issue.Key, StringComparison.OrdinalIgnoreCase) &&
                            DateOnly.FromDateTime(p.StartedUtc.ToLocalTime().DateTime) == date)
                .Select(p => new WorklogRow(p.StartedUtc.ToLocalTime(), p.Seconds, "You", "Pending", p.LastError)))
            .OrderBy(r => r.Started)
            .ToList();

        DayGroup.Header = selected.ToString("dddd, d MMMM yyyy");
        WorklogGrid.ItemsSource = rows.Select(r => new DisplayRow(
            r.Started.ToString("HH:mm"), BarViewModel.FormatElapsed(TimeSpan.FromSeconds(r.Seconds)),
            r.Author, r.Status, r.Comment ?? string.Empty)).ToList();
        DayTotal.Text = $"Total: {BarViewModel.FormatElapsed(TimeSpan.FromSeconds(rows.Sum(r => r.Seconds)))}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _closed.Cancel();
        _load?.Cancel();
        _closed.Dispose();
    }

    private sealed record WorklogRow(DateTimeOffset Started, int Seconds, string Author, string Status, string? Comment);
    private sealed record DisplayRow(string Time, string Duration, string Author, string Status, string Comment);
}
