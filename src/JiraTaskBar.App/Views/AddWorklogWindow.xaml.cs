using System.ComponentModel;
using System.Globalization;
using System.Windows;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Tracking;
using Wpf.Ui.Controls;

namespace JiraTaskBar.App.Views;

public partial class AddWorklogWindow
{
    private readonly IJiraClient _client;
    private readonly JiraWorklog? _original;
    private bool _worklogSaved;
    private bool _saving;

    public AddWorklogWindow(IReadOnlyList<JiraIssue> issues, JiraIssue selected,
        DateTimeOffset started, TimeSpan duration, IJiraClient client, JiraWorklog? original = null)
    {
        InitializeComponent();
        _client = client;
        _original = original;
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
        if (original is not null)
        {
            Title = DialogTitle.Title = "Edit worklog";
            TicketBox.IsEnabled = false;
            DescriptionBox.Text = original.Comment ?? string.Empty;
        }
    }

    public bool WorklogSaved => _worklogSaved;
    public JiraIssue? SavedIssue { get; private set; }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_worklogSaved || _saving || !TryRead(out var issue, out var started, out var seconds)) return;
        _saving = true;
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        Status.Severity = InfoBarSeverity.Informational;
        Status.Message = "Saving to Jira…";
        Status.IsOpen = true;
        var description = DescriptionBox.Text.Trim();
        var asTicketComment = DescriptionTargetBox.SelectedIndex == 1;
        try
        {
            if (_original is { } original)
            {
                var latest = (await _client.GetWorklogsAsync(issue.Key)).FirstOrDefault(w => w.Id == original.Id);
                if (latest is null || latest.Started != original.Started ||
                    latest.TimeSpentSeconds != original.TimeSpentSeconds || latest.Comment != original.Comment ||
                    !System.Text.Json.Nodes.JsonNode.DeepEquals(latest.CommentDocument, original.CommentDocument))
                {
                    Invalid("This worklog changed in Jira. Close the editor and refresh before editing it again.");
                    SaveButton.IsEnabled = CancelButton.IsEnabled = true;
                    return;
                }
                var changedDescription = !asTicketComment && description != (original.Comment ?? string.Empty).Trim();
                await _client.UpdateWorklogAsync(issue.Key, changedDescription
                    ? latest with { Comment = description, CommentDocument = null } : latest, started, seconds);
            }
            else
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
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Status.Severity = InfoBarSeverity.Error;
            Status.Message = $"Could not save the worklog: {ex.Message}";
            SaveButton.IsEnabled = CancelButton.IsEnabled = true;
        }
        finally
        {
            _saving = false;
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
        if (_original is { } original)
        {
            // Keep sub-minute precision when a field was only viewed, not edited.
            var originalLocal = original.Started.LocalDateTime;
            if (date.Date == originalLocal.Date &&
                StartTimeBox.Text == originalLocal.ToString("t", CultureInfo.CurrentCulture))
                started = original.Started;
            var originalDuration = TimeSpan.FromSeconds(original.TimeSpentSeconds);
            if (DurationBox.Text == $"{(int)originalDuration.TotalHours}:{originalDuration.Minutes:00}")
                seconds = original.TimeSpentSeconds;
        }
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

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_saving) return;
        e.Cancel = true;
        Status.Severity = InfoBarSeverity.Informational;
        Status.Message = "Please wait for Jira to finish saving the worklog.";
        Status.IsOpen = true;
    }

    private sealed record TicketChoice(JiraIssue Issue, string Display);
}
