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
        panel.Children.Add(_date);
        _time = new TextBox { Name = "StartTimeBox", Text = _originalTimeText, Margin = new Thickness(0, 6, 0, 12) };
        panel.Children.Add(_time);
        panel.Children.Add(new TextBlock { Text = "Duration (hours:minutes:seconds)" });
        _duration = new TextBox { Name = "DurationBox", Text = _originalDurationText, Margin = new Thickness(0, 6, 0, 12) };
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
            if (!TimeSpan.TryParse(_duration.Text.Trim(), CultureInfo.InvariantCulture, out var duration) ||
                duration < TimeSpan.FromMinutes(1) || duration.TotalSeconds > int.MaxValue)
            {
                _error.Text = "Enter a duration of at least 1 minute, for example 1:30:00.";
                return;
            }
            seconds = (int)Math.Round(duration.TotalSeconds);
        }

        // Keep exact timestamps (including the offset during a repeated DST hour) and
        // durations when their fields are untouched.
        Started = date == _originalStarted.LocalDateTime.Date && _time.Text == _originalTimeText
            ? _originalStarted
            : WorklogCalendarLayout.LocalDateTime(DateOnly.FromDateTime(date), time, TimeZoneInfo.Local);
        Seconds = seconds;
        DialogResult = true;
    }
}
