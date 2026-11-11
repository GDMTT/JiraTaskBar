using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.App.Views;

/// <summary>Collects time changes; the calendar checks Jira for conflicts before saving.</summary>
public sealed class EditWorklogWindow : Window
{
    private readonly DatePicker _date;
    private readonly TextBox _time;
    private readonly TextBox _duration;
    private readonly TextBlock _error;
    private readonly DateTimeOffset _originalStarted;
    private readonly int _originalSeconds;
    private readonly string _originalTimeText;
    private readonly string _originalDurationText;

    public EditWorklogWindow(string issueKey, DateTimeOffset started, int seconds, string comment)
    {
        Title = $"Edit worklog – {issueKey}";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        _originalStarted = Started = started;
        _originalSeconds = Seconds = seconds;
        _originalTimeText = started.LocalDateTime.ToString("T", CultureInfo.CurrentCulture);
        var duration = TimeSpan.FromSeconds(seconds);
        _originalDurationText = $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";

        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = panel;
        panel.Children.Add(new TextBlock { Text = "Date and local start time" });
        _date = new DatePicker { Name = "StartDate", SelectedDate = started.LocalDateTime.Date };
        System.Windows.Automation.AutomationProperties.SetName(_date, "Start date");
        panel.Children.Add(_date);
        _time = new TextBox { Name = "StartTimeBox", Text = _originalTimeText, Margin = new Thickness(0, 6, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(_time, "Local start time");
        panel.Children.Add(_time);
        panel.Children.Add(new TextBlock { Text = "Duration (hours:minutes:seconds)" });
        _duration = new TextBox { Name = "DurationBox", Text = _originalDurationText, Margin = new Thickness(0, 6, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(_duration, "Duration (hours:minutes:seconds)");
        panel.Children.Add(_duration);
        panel.Children.Add(new TextBlock { Text = "Worklog description", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBox
        {
            Text = comment, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 6, 0, 12),
        });
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var save = new Button { Name = "SaveButton", Content = "Save", IsDefault = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += Save_Click;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
    }

    public DateTimeOffset Started { get; private set; }
    public int Seconds { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_date.SelectedDate is not { } date ||
            !TimeOnly.TryParse(_time.Text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var time))
        {
            _error.Text = "Enter a valid local date and start time.";
            return;
        }
        var seconds = _originalSeconds;
        if (_duration.Text != _originalDurationText)
        {
            if (!TryDurationSeconds(_duration.Text, out seconds))
            {
                _error.Text = "Enter a duration of at least 1 minute, for example 1:30:00.";
                return;
            }
        }

        // Keep exact timestamps (including the offset during a repeated DST hour) and
        // durations when their fields are untouched.
        Started = date == _originalStarted.LocalDateTime.Date && _time.Text == _originalTimeText
            ? _originalStarted
            : WorklogCalendarLayout.LocalDateTime(DateOnly.FromDateTime(date), time, TimeZoneInfo.Local);
        Seconds = seconds;
        DialogResult = true;
    }

    private static bool TryDurationSeconds(string text, out int seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is not (2 or 3) ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            minutes > 59) return false;
        var remainder = 0;
        if (parts.Length == 3 &&
            (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out remainder) || remainder > 59))
            return false;
        var total = (long)hours * 3600 + minutes * 60 + remainder;
        if (total < 60 || total > int.MaxValue) return false;
        seconds = (int)total;
        return true;
    }
}
