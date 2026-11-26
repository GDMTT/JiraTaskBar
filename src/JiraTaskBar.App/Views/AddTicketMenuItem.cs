using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using JiraTaskBar.App.Services;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.Views;

internal sealed class AddTicketMenuItem : MenuItem
{
    private readonly TextBox _query = new() { MaxLength = TicketInput.MaxSearchLength, MinWidth = 150 };
    private readonly Button _find = new() { Content = "Search", Margin = new Thickness(4, 0, 0, 0) };
    private readonly Button _add = new() { Content = "Add", Margin = new Thickness(4, 0, 0, 0) };
    private readonly ListBox _matches = new() { MaxHeight = 160, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { MaxWidth = 310, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly AppController _app;
    private readonly IJiraClient? _client;
    private bool _busy;

    public AddTicketMenuItem(AppController app, IJiraClient? client)
    {
        _app = app;
        _client = client;
        StaysOpenOnClick = true;
        Focusable = false;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Ticket key or summary" });
        AutomationProperties.SetName(_query, "Ticket key or summary");
        AutomationProperties.SetName(_matches, "Matching Jira tickets");
        var row = new DockPanel();
        DockPanel.SetDock(_add, Dock.Right);
        DockPanel.SetDock(_find, Dock.Right);
        row.Children.Add(_add);
        row.Children.Add(_find);
        row.Children.Add(_query);
        panel.Children.Add(row);
        panel.Children.Add(_matches);
        panel.Children.Add(_status);
        Header = panel;

        _query.TextChanged += (_, _) =>
        {
            ClearMatches();
            SetStatus(string.Empty);
            UpdateButtons();
        };
        _query.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || _busy || _client is null) return;
            e.Handled = true;
            if (TicketInput.ParseKey(_query.Text.Trim()) is not null) await AddAsync();
            else if (_find.IsEnabled) await SearchAsync();
        };
        _matches.SelectionChanged += (_, _) => UpdateButtons();
        _find.Click += async (_, _) => await SearchAsync();
        _add.Click += async (_, _) => await AddAsync();
        _query.IsEnabled = client is not null;
        SetStatus(client is null ? "Connect to Jira in Settings to add tickets." : string.Empty);
        UpdateButtons();
    }

    private async Task SearchAsync()
    {
        if (_client is not { } client || _busy) return;
        var query = _query.Text.Trim();
        if (query.Length < 2 || query.Any(char.IsControl)) return;
        ClearMatches();
        SetBusy(true);
        SetStatus("Searching Jira…");
        try
        {
            var results = await _app.SearchTicketSummariesAsync(client, query);
            if (!ReferenceEquals(client, _app.Client))
            {
                SetStatus("The Jira connection changed. Reopen the menu to try again.");
                return;
            }

            foreach (var issue in results.Take(10))
                _matches.Items.Add(new ListBoxItem { Content = $"{issue.Key} · {issue.Summary}", Tag = issue });
            _matches.Visibility = _matches.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            SetStatus(_matches.Items.Count == 0 ? "No matching tickets." : string.Empty);
        }
        catch (Exception ex)
        {
            SetStatus(_app.ReportTicketLookupFailure(client, ex));
        }
        finally { SetBusy(false); }
    }

    private async Task AddAsync()
    {
        if (_client is not { } client || _busy) return;
        SetBusy(true);
        SetStatus("Adding ticket…");
        try
        {
            var result = _matches.SelectedItem is ListBoxItem { Tag: JiraIssue issue }
                ? _app.PinTicket(client, issue)
                : TicketInput.ParseKey(_query.Text.Trim()) is { } key
                    ? await _app.AddTicketByKeyAsync(client, key)
                    : ("Enter a ticket key or select a search result.", false);
            if (result.Item2)
            {
                _query.Clear();
                ClearMatches();
            }
            SetStatus(result.Item1);
        }
        finally { SetBusy(false); }
    }

    private void ClearMatches()
    {
        _matches.Items.Clear();
        _matches.Visibility = Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _query.IsEnabled = !busy;
        _matches.IsEnabled = !busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var query = _query.Text.Trim();
        var key = TicketInput.ParseKey(query);
        _find.IsEnabled = _client is not null && !_busy && key is null && query.Length >= 2 && !query.Any(char.IsControl);
        _add.IsEnabled = _client is not null && !_busy && (key is not null || _matches.SelectedItem is not null);
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
