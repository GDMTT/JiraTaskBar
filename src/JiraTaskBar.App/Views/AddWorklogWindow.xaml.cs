using System.Globalization;
using System.Windows;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Tracking;
using Wpf.Ui.Controls;

namespace JiraTaskBar.App.Views;

public partial class AddWorklogWindow
{
    private readonly IJiraClient _client;
    private bool _worklogSaved;

    public AddWorklogWindow(IReadOnlyList<JiraIssue> issues, JiraIssue selected,
        DateTimeOffset started, TimeSpan duration, IJiraClient client)
    {
        InitializeComponent();
        _client = client;
        var choices = issues
            .Append(selected)
            .DistinctBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
            .Select(i => new TicketChoice(i, $"{i.Key}  {i.Summary}"))
            .OrderBy(i => i.Issue.PriorityRank)
            .ThenBy(i => i.Issue.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        TicketBox.ItemsSource = choices;
        TicketBox.SelectedItem = choices.First(i => i.Issue.Key.Equals(selected.Key, StringComparison.OrdinalIgnoreCase));
        StartDate.SelectedDate = started.LocalDateTime.Date;
        StartTimeBox.Text = started.LocalDateTime.ToString("t", CultureInfo.CurrentCulture);
        DurationBox.Text = $"{(int)duration.TotalHours}:{duration.Minutes:00}";
        DescriptionTargetBox.SelectedIndex = 0;
    }

    public bool WorklogSaved => _worklogSaved;
    public JiraIssue? SavedIssue { get; private set; }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_worklogSaved || !TryRead(out var issue, out var started, out var seconds)) return;
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        Status.Severity = InfoBarSeverity.Informational;
        Status.Message = "Saving to Jira…";
        Status.IsOpen = true;
        var description = DescriptionBox.Text.Trim();
        var asTicketComment = DescriptionTargetBox.SelectedIndex == 1;
        try
        {
            await _client.AddWorklogAsync(issue.Key, started, seconds,
                asTicketComment || description.Length == 0 ? null : description);
            _worklogSaved = true;
            SavedIssue = issue;

            if (asTicketComment && description.Length > 0)
            {
                try
                {
                    // Internal is the safe default for Jira Service Management; other projects ignore it.
                    await _client.AddCommentAsync(issue.Key, description, isInternal: true);
                }
                catch (Exception ex)
                {
                    Status.Severity = InfoBarSeverity.Warning;
                    Status.Message = $"The worklog was saved, but its ticket comment was not: {ex.Message}";
                    CancelButton.Content = "Close";
                    CancelButton.IsEnabled = true;
                    return;
                }
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Status.Severity = InfoBarSeverity.Error;
            Status.Message = $"Could not save the worklog: {ex.Message}";
            SaveButton.IsEnabled = CancelButton.IsEnabled = true;
        }
    }

    private bool TryRead(out JiraIssue issue, out DateTimeOffset started, out int seconds)
    {
        issue = (TicketBox.SelectedItem as TicketChoice)?.Issue!;
        started = default;
        seconds = 0;
        if (issue is null)
            return Invalid("Select a ticket.");
        if (StartDate.SelectedDate is not { } date ||
            !TimeOnly.TryParse(StartTimeBox.Text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var time))
            return Invalid("Enter a valid local date and start time.");
        if (!TryDuration(DurationBox.Text, out var duration) || duration < TimeSpan.FromMinutes(1) || duration > TimeSpan.FromDays(1))
            return Invalid("Duration must be between 1 minute and 24 hours, for example 1:30.");

        started = WorklogCalendarLayout.LocalDateTime(
            DateOnly.FromDateTime(date), time, TimeZoneInfo.Local);
        seconds = (int)Math.Round(duration.TotalSeconds);
        return true;
    }

    private bool Invalid(string message)
    {
        Status.Severity = InfoBarSeverity.Error;
        Status.Message = message;
        Status.IsOpen = true;
        return false;
    }

    private static bool TryDuration(string text, out TimeSpan duration)
    {
        if (TimeSpan.TryParse(text.Trim(), CultureInfo.CurrentCulture, out duration)) return true;
        return TimeSpan.TryParseExact(text.Trim(), ["h\\:mm", "hh\\:mm"],
            CultureInfo.InvariantCulture, out duration);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record TicketChoice(JiraIssue Issue, string Display);
}
