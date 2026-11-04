using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JiraTaskBar.App.ViewModels;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.App.Views;

public partial class WorklogCalendarWindow
{
    private static readonly Brush GridLine = MakeBrush(0x28, 0x80, 0x80, 0x80);
    private static readonly Brush TodayFill = MakeBrush(0x24, 0xFF, 0xCC, 0x33);
    private static readonly Brush WeekendFill = MakeBrush(0x16, 0x80, 0x80, 0x80);
    private static readonly Brush SavedFill = MakeBrush(0xD8, 0x57, 0x68, 0xE8);
    private static readonly Brush PendingFill = MakeBrush(0xDF, 0xE6, 0x9F, 0x20);

    private JiraIssue _issue;
    private readonly IJiraClient _client;
    private readonly Func<IReadOnlyList<PendingWorklog>> _pending;
    private readonly Func<IReadOnlyList<JiraIssue>> _issues;
    private readonly CancellationTokenSource _closed = new();
    private readonly int _resolutionMinutes;
    private CancellationTokenSource? _load;
    private int _loadVersion;
    private bool _isClosed;
    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);
    private WorklogCalendarView _view = WorklogCalendarView.Week;
    private IReadOnlyList<JiraWorklog> _worklogs = [];
    private ScrollViewer? _timeScroll;
    private Grid? _timeGrid;
    private WorklogCalendarRange? _timeRange;
    private double _slotHeight;
    private TimeSlot? _selectionStart;
    private Border? _selectionBlock;

    public WorklogCalendarWindow(JiraIssue issue, IJiraClient client,
        Func<IReadOnlyList<PendingWorklog>> pending, Func<IReadOnlyList<JiraIssue>>? issues = null,
        int resolutionMinutes = 30)
    {
        InitializeComponent();
        _issue = issue;
        _client = client;
        _pending = pending;
        _issues = issues ?? (() => [issue]);
        _resolutionMinutes = WorklogCalendarLayout.NormalizeResolution(resolutionMinutes);
        WindowTitle.Title = $"Time calendar – {issue.Key}";
        TicketTitle.Text = $"{issue.Key}  {issue.Summary}";
        ResolutionText.Text = $"{_resolutionMinutes}-minute resolution";
        ViewModeBox.SelectedIndex = 1;
        RenderScheduler(resetScroll: true);
    }

    public string IssueKey => _issue.Key;

    public void FocusIssue(JiraIssue issue)
    {
        var changed = !_issue.Key.Equals(issue.Key, StringComparison.OrdinalIgnoreCase);
        _issue = issue;
        WindowTitle.Title = $"Time calendar – {issue.Key}";
        TicketTitle.Text = $"{issue.Key}  {issue.Summary}";
        if (changed) _ = RefreshAsync();
        else RenderScheduler();
    }

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
            RenderScheduler();
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

    private void ViewMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ViewModeBox.SelectedIndex < 0) return;
        _view = (WorklogCalendarView)ViewModeBox.SelectedIndex;
        RenderScheduler(resetScroll: true);
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _anchor = WorklogCalendarLayout.Move(_anchor, _view, -1);
        RenderScheduler(resetScroll: true);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _anchor = WorklogCalendarLayout.Move(_anchor, _view, 1);
        RenderScheduler(resetScroll: true);
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _anchor = DateOnly.FromDateTime(DateTime.Today);
        RenderScheduler(resetScroll: true);
    }

    private void RenderScheduler(bool resetScroll = false)
    {
        if (SchedulerHost is null) return;
        var range = WorklogCalendarLayout.RangeFor(_view, _anchor,
            CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
        RangeTitle.Text = RangeLabel(range);
        var entries = Entries()
            .Where(e => e.Date >= range.Start && e.Date < range.EndExclusive)
            .OrderBy(e => e.Started)
            .ToList();
        RangeTotal.Text = $"Total: {FormatDuration(entries.Sum(e => e.Seconds))}";

        SchedulerHost.Children.Clear();
        _timeScroll = null;
        if (_view == WorklogCalendarView.Month)
            RenderMonth(range, entries);
        else
            RenderTimeGrid(range, entries, resetScroll);
    }

    private void RenderMonth(WorklogCalendarRange range, IReadOnlyList<CalendarEntry> entries)
    {
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var back = (7 + (int)range.Start.DayOfWeek - (int)firstDay) % 7;
        var gridStart = range.Start.AddDays(-back);
        var grid = new Grid();
        for (var column = 0; column < 7; column++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var row = 0; row < 6; row++) grid.RowDefinitions.Add(new RowDefinition());

        for (var column = 0; column < 7; column++)
        {
            var day = (DayOfWeek)(((int)firstDay + column) % 7);
            var header = new TextBlock
            {
                Text = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 6, 4, 6),
            };
            Grid.SetColumn(header, column);
            grid.Children.Add(header);
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var cell = 0; cell < 42; cell++)
        {
            var date = gridStart.AddDays(cell);
            var inMonth = date.Month == _anchor.Month && date.Year == _anchor.Year;
            var dayEntries = entries.Where(e => e.Date == date).ToList();
            var panel = new StackPanel { Margin = new Thickness(5) };
            panel.Children.Add(new TextBlock
            {
                Text = date.Day.ToString(CultureInfo.CurrentCulture),
                FontWeight = date == today ? FontWeights.Bold : FontWeights.Normal,
                Opacity = inMonth ? 1 : 0.4,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            if (dayEntries.Count > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"Logged: {FormatDuration(dayEntries.Sum(e => e.Seconds))}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 3, 0, 2),
                });
                foreach (var entry in dayEntries.Take(4)) panel.Children.Add(MonthEntry(entry));
                if (dayEntries.Count > 4)
                    panel.Children.Add(new TextBlock { Text = $"+{dayEntries.Count - 4} more", FontSize = 11, Opacity = 0.65 });
            }

            var border = new Border
            {
                BorderBrush = GridLine,
                BorderThickness = new Thickness(0.5),
                Background = date == today ? TodayFill :
                    date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? WeekendFill : Brushes.Transparent,
                Child = panel,
                Tag = date,
            };
            border.MouseLeftButtonDown += MonthDay_Click;
            Grid.SetColumn(border, cell % 7);
            Grid.SetRow(border, cell / 7 + 1);
            grid.Children.Add(border);
        }
        SchedulerHost.Children.Add(grid);
    }

    private TextBlock MonthEntry(CalendarEntry entry) => new()
    {
        Text = $"{entry.Started:HH:mm}  {FormatDuration(entry.Seconds)}  {entry.Comment}",
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 11,
        Margin = new Thickness(0, 1, 0, 1),
        Foreground = entry.Pending ? PendingFill : SavedFill,
        ToolTip = EntryTooltip(entry),
    };

    private void MonthDay_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: DateOnly date }) return;
        _anchor = date;
        if (e.ClickCount >= 2)
        {
            _view = WorklogCalendarView.Day;
            ViewModeBox.SelectedIndex = (int)_view;
        }
        else
        {
            RenderScheduler();
        }
    }

    private void RenderTimeGrid(WorklogCalendarRange range, IReadOnlyList<CalendarEntry> entries, bool resetScroll)
    {
        var days = range.DayCount;
        var slots = 24 * 60 / _resolutionMinutes;
        var slotHeight = Math.Max(8, _resolutionMinutes * 1.2);
        var grid = new Grid { MinWidth = 680, AllowDrop = true };
        _timeGrid = grid;
        _timeRange = range;
        _slotHeight = slotHeight;
        grid.MouseLeftButtonDown += TimeGrid_MouseLeftButtonDown;
        grid.MouseMove += TimeGrid_MouseMove;
        grid.MouseLeftButtonUp += TimeGrid_MouseLeftButtonUp;
        grid.LostMouseCapture += TimeGrid_LostMouseCapture;
        grid.DragOver += TimeGrid_DragOver;
        grid.Drop += TimeGrid_Drop;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        for (var day = 0; day < days; day++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        for (var slot = 0; slot < slots; slot++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(slotHeight) });

        for (var day = 0; day < days; day++)
        {
            var date = range.Start.AddDays(day);
            var header = new Border
            {
                BorderBrush = GridLine,
                BorderThickness = new Thickness(0.5),
                Background = date == DateOnly.FromDateTime(DateTime.Today) ? TodayFill : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = date.ToString(days == 1 ? "dddd, d MMMM" : "ddd, d MMM", CultureInfo.CurrentCulture),
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(header, day + 1);
            grid.Children.Add(header);
        }

        for (var slot = 0; slot < slots; slot++)
        {
            var minute = slot * _resolutionMinutes;
            if (minute % 60 == 0)
            {
                var label = new TextBlock
                {
                    Text = DateTime.Today.AddMinutes(minute).ToString("t", CultureInfo.CurrentCulture),
                    FontSize = 11,
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -7, 6, 0),
                };
                Grid.SetRow(label, slot + 1);
                grid.Children.Add(label);
            }
            for (var day = 0; day < days; day++)
            {
                var cell = new Border
                {
                    BorderBrush = GridLine,
                    BorderThickness = new Thickness(0.5, minute % 60 == 0 ? 0.7 : 0.25, 0.5, 0.25),
                    Background = range.Start.AddDays(day) == DateOnly.FromDateTime(DateTime.Today)
                        ? TodayFill : Brushes.Transparent,
                };
                Grid.SetColumn(cell, day + 1);
                Grid.SetRow(cell, slot + 1);
                grid.Children.Add(cell);
            }
        }

        foreach (var entry in entries)
        {
            var day = entry.Date.DayNumber - range.Start.DayNumber;
            if (day < 0 || day >= days) continue;
            var startSlot = WorklogCalendarLayout.SlotIndex(TimeOnly.FromDateTime(entry.Started.DateTime), _resolutionMinutes);
            var span = Math.Min(slots - startSlot,
                WorklogCalendarLayout.SlotSpan(entry.Seconds, _resolutionMinutes));
            var block = WorklogBlock(entry);
            Grid.SetColumn(block, day + 1);
            Grid.SetRow(block, startSlot + 1);
            Grid.SetRowSpan(block, Math.Max(1, span));
            Panel.SetZIndex(block, 2);
            grid.Children.Add(block);
        }

        _timeScroll = new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        SchedulerHost.Children.Add(_timeScroll);
        if (resetScroll)
            Dispatcher.BeginInvoke(() => _timeScroll?.ScrollToVerticalOffset(8 * 60 / _resolutionMinutes * slotHeight),
                DispatcherPriority.Loaded);
    }

    private Border WorklogBlock(CalendarEntry entry)
    {
        var text = new TextBlock
        {
            Text = $"{entry.Started:HH:mm}–{entry.Ended:HH:mm}  {FormatDuration(entry.Seconds)}\n{_issue.Key}  {entry.Comment}",
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.White,
            FontSize = 11,
            Margin = new Thickness(6, 3, 6, 3),
        };
        var block = new Border
        {
            Margin = new Thickness(2, 1, 2, 1),
            CornerRadius = new CornerRadius(4),
            Background = entry.Pending ? PendingFill : SavedFill,
            BorderBrush = entry.Pending ? Brushes.DarkOrange : Brushes.RoyalBlue,
            BorderThickness = new Thickness(1),
            ToolTip = EntryTooltip(entry),
            Child = text,
            Tag = entry,
        };
        block.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return block;
    }

    private void TimeGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid grid || TimeSlotAt(grid, e.GetPosition(grid)) is not { } slot) return;
        _selectionStart = slot;
        grid.CaptureMouse();
        UpdateSelection(slot);
        e.Handled = true;
    }

    private void TimeGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (_selectionStart is null || e.LeftButton != MouseButtonState.Pressed || sender is not Grid grid) return;
        if (TimeSlotAt(grid, e.GetPosition(grid)) is { } slot) UpdateSelection(slot);
    }

    private async void TimeGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectionStart is not { } start || sender is not Grid grid) return;
        var end = TimeSlotAt(grid, e.GetPosition(grid)) ?? start;
        var lastSlot = end.Date == start.Date ? end.Slot : start.Slot;
        var first = Math.Min(start.Slot, lastSlot);
        var count = Math.Abs(lastSlot - start.Slot) + 1;
        ClearSelection(grid);
        await ShowAddWorklogAsync(start.Date, first, count, _issue);
        e.Handled = true;
    }

    private void TimeGrid_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is Grid grid && _selectionStart is not null) ClearSelection(grid);
    }

    private void UpdateSelection(TimeSlot current)
    {
        if (_timeGrid is not { } grid || _selectionStart is not { } start) return;
        var slot = current.Date == start.Date ? current.Slot : start.Slot;
        var first = Math.Min(start.Slot, slot);
        var count = Math.Abs(slot - start.Slot) + 1;
        if (_selectionBlock is null)
        {
            _selectionBlock = new Border
            {
                Margin = new Thickness(2, 1, 2, 1),
                CornerRadius = new CornerRadius(4),
                Background = MakeBrush(0x70, 0x38, 0x9B, 0x5A),
                BorderBrush = Brushes.SeaGreen,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
            };
            Panel.SetZIndex(_selectionBlock, 4);
            grid.Children.Add(_selectionBlock);
        }
        var day = start.Date.DayNumber - _timeRange!.Start.DayNumber;
        Grid.SetColumn(_selectionBlock, day + 1);
        Grid.SetRow(_selectionBlock, first + 1);
        Grid.SetRowSpan(_selectionBlock, count);
    }

    private void ClearSelection(Grid grid)
    {
        grid.ReleaseMouseCapture();
        if (_selectionBlock is not null) grid.Children.Remove(_selectionBlock);
        _selectionBlock = null;
        _selectionStart = null;
    }

    private TimeSlot? TimeSlotAt(Grid grid, Point point)
    {
        if (_timeRange is null || point.X < 64 || point.Y < 36 || grid.ActualWidth <= 64) return null;
        var days = _timeRange.DayCount;
        var dayWidth = (grid.ActualWidth - 64) / days;
        var day = Math.Clamp((int)((point.X - 64) / dayWidth), 0, days - 1);
        var slots = 24 * 60 / _resolutionMinutes;
        var slot = Math.Clamp((int)((point.Y - 36) / _slotHeight), 0, slots - 1);
        return new TimeSlot(_timeRange.Start.AddDays(day), slot);
    }

    private void TimeGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TicketDragFormat) && sender is Grid grid &&
                    TimeSlotAt(grid, e.GetPosition(grid)) is not null
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void TimeGrid_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Grid grid || TimeSlotAt(grid, e.GetPosition(grid)) is not { } slot ||
            e.Data.GetData(TicketDragFormat) is not string key) return;
        var issue = _issues().FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (issue is null) return;
        await ShowAddWorklogAsync(slot.Date, slot.Slot, 1, issue);
        e.Handled = true;
    }

    private async void NewWorklog_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        var slot = WorklogCalendarLayout.SlotIndex(TimeOnly.FromDateTime(now), _resolutionMinutes);
        await ShowAddWorklogAsync(_anchor, slot, 1, _issue);
    }

    private async Task ShowAddWorklogAsync(DateOnly date, int startSlot, int slotCount, JiraIssue selected)
    {
        var time = TimeOnly.MinValue.AddMinutes(startSlot * _resolutionMinutes);
        var started = WorklogCalendarLayout.LocalDateTime(date, time, TimeZoneInfo.Local);
        var dialog = new AddWorklogWindow(_issues(), selected, started,
            TimeSpan.FromMinutes(Math.Max(1, slotCount) * _resolutionMinutes), _client) { Owner = this };
        dialog.ShowDialog();
        if (!dialog.WorklogSaved || dialog.SavedIssue is not { } saved) return;
        if (!_issue.Key.Equals(saved.Key, StringComparison.OrdinalIgnoreCase))
            FocusIssue(saved);
        else
            await RefreshAsync();
    }

    private string EntryTooltip(CalendarEntry entry) =>
        $"{_issue.Key}\n{entry.Started:dddd, d MMMM yyyy HH:mm}–{entry.Ended:HH:mm}\n" +
        $"{FormatDuration(entry.Seconds)} · {(entry.Pending ? "Pending locally" : "Saved in Jira")}" +
        (string.IsNullOrWhiteSpace(entry.Comment) ? string.Empty : $"\n{entry.Comment}");

    private IReadOnlyList<CalendarEntry> Entries() => _worklogs
        .Select(w => new CalendarEntry(w.Started.ToLocalTime(), w.TimeSpentSeconds, w.Comment ?? string.Empty, false))
        .Concat(_pending()
            .Where(p => p.Key.Equals(_issue.Key, StringComparison.OrdinalIgnoreCase))
            .Select(p => new CalendarEntry(p.StartedUtc.ToLocalTime(), p.Seconds, p.LastError ?? string.Empty, true)))
        .ToList();

    private string RangeLabel(WorklogCalendarRange range) => _view switch
    {
        WorklogCalendarView.Day => range.Start.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture),
        WorklogCalendarView.Week => $"{range.Start:dd MMM} – {range.EndExclusive.AddDays(-1):dd MMM yyyy}",
        WorklogCalendarView.Month => range.Start.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
        _ => string.Empty,
    };

    private static string FormatDuration(int seconds) =>
        BarViewModel.FormatElapsed(TimeSpan.FromSeconds(seconds));

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _closed.Cancel();
        _load?.Cancel();
        _closed.Dispose();
    }

    private static SolidColorBrush MakeBrush(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed record CalendarEntry(DateTimeOffset Started, int Seconds, string Comment, bool Pending)
    {
        public DateOnly Date => DateOnly.FromDateTime(Started.DateTime);
        public DateTimeOffset Ended => Started.AddSeconds(Seconds);
    }

    private sealed record TimeSlot(DateOnly Date, int Slot);

    public const string TicketDragFormat = "JiraTaskBar.TicketKey";
}
