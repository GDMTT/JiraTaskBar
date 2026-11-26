using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using JiraTaskBar.App.Services;
using JiraTaskBar.App.ViewModels;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Storage;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.App.Views;

public partial class WorklogCalendarWindow
{
    private static readonly Brush GridLine = MakeBrush(0x28, 0x80, 0x80, 0x80);
    private static readonly Brush HourLine = MakeBrush(0xB0, 0x80, 0x80, 0x80);
    private static readonly Brush HalfHourLine = MakeBrush(0x80, 0x80, 0x80, 0x80);
    private static readonly Brush ResolutionLine = MakeBrush(0x45, 0x80, 0x80, 0x80);
    private static readonly Brush TodayFill = MakeBrush(0x24, 0xFF, 0xCC, 0x33);
    private static readonly Brush WorkdayFill = MakeBrush(0x18, 0, 0, 0);
    private static readonly Color DefaultWorklogColor = Color.FromRgb(0x5E, 0x6A, 0x7A);

    private JiraIssue _issue;
    private readonly IJiraClient _client;
    private readonly Func<IReadOnlyList<PendingWorklog>> _pending;
    private readonly Func<IReadOnlyList<JiraIssue>> _issues;
    private readonly Func<ActiveSession?> _active;
    private readonly Func<AppSettings?> _settings;
    private readonly CancellationTokenSource _closed = new();
    private readonly int _resolutionMinutes;
    private readonly DispatcherTimer _currentTimeTimer = new();
    private Border? _currentTimeLine;
    private CancellationTokenSource? _load;
    private int _loadVersion;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private const int CachedRangeLimit = 24;
    private readonly Dictionary<WorklogCalendarRange, (WorklogCalendarLoadResult Result, DateTimeOffset LoadedAt)> _rangeCache = [];
    private bool _isClosed;
    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);
    private WorklogCalendarView _view = WorklogCalendarView.Week;
    private IReadOnlyList<JiraWorklog> _worklogs = [];
    private IReadOnlyList<JiraIssue> _worklogIssues = [];
    private string? _accountId;
    private readonly Dictionary<string, Color> _worklogColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _localIssueKeys = new(StringComparer.OrdinalIgnoreCase);
    private string? _highlightedIssueKey;
    private bool _updatingHighlight;
    private bool _localChangePending;
    private ScrollViewer? _timeScroll;
    private double _verticalOffset;
    private Grid? _timeGrid;
    private WorklogCalendarRange? _timeRange;
    private DateOnly[] _timeDays = [];
    private WorklogCalendarHours _timeHours = WorklogCalendarHours.AllDay;
    private double _slotHeight;
    private TimeSlot? _selectionStart;
    private Border? _selectionBlock;
    private CalendarEdit? _calendarEdit;
    private bool _savingEdit;
    private bool _refreshPending;
    private JiraIssue? _pendingFocusIssue;

    public WorklogCalendarWindow(JiraIssue issue, IJiraClient client,
        Func<IReadOnlyList<PendingWorklog>> pending, Func<IReadOnlyList<JiraIssue>>? issues = null,
        int resolutionMinutes = 30, Func<ActiveSession?>? active = null, Func<AppSettings?>? settings = null)
    {
        InitializeComponent();
        _issue = issue;
        _client = client;
        _pending = pending;
        _issues = issues ?? (() => [issue]);
        _active = active ?? (() => null);
        _settings = settings ?? (() => null);
        _resolutionMinutes = WorklogCalendarLayout.NormalizeResolution(resolutionMinutes);
        WindowTitle.Title = $"Time calendar – {issue.Key}";
        TicketTitle.Text = $"{issue.Key}  {issue.Summary}";
        ResolutionText.Text = $"{_resolutionMinutes}-minute resolution";
        ViewModeBox.SelectedIndex = 1;
        RenderScheduler(resetScroll: true);
        _currentTimeTimer.Interval = TimeSpan.FromMinutes(1);
        _currentTimeTimer.Tick += CurrentTimeTimer_Tick;
    }

    public string IssueKey => _issue.Key;

    public void FocusIssue(JiraIssue issue)
    {
        if (_calendarEdit is not null || _savingEdit)
        {
            _pendingFocusIssue = issue;
            StatusText.Text = $"Finishing the current edit before opening {issue.Key}…";
            return;
        }
        ApplyFocusIssue(issue);
    }

    private void ApplyFocusIssue(JiraIssue issue)
    {
        var changed = !_issue.Key.Equals(issue.Key, StringComparison.OrdinalIgnoreCase);
        _issue = issue;
        WindowTitle.Title = $"Time calendar – {issue.Key}";
        TicketTitle.Text = $"{issue.Key}  {issue.Summary}";
        if (changed)
        {
            RenderScheduler();
            _ = RefreshAsync();
        }
        else RenderScheduler();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateCurrentTime(DateTime.Now);
        _currentTimeTimer.Start();
        await RefreshAsync();
    }

    private void CurrentTimeTimer_Tick(object? sender, EventArgs e)
    {
        if (_active() is not null || _localChangePending) RefreshLocalEntries();
        UpdateCurrentTime(DateTime.Now);
    }

    internal void RefreshLocalEntries()
    {
        if (_isClosed) return;
        if (_calendarEdit is not null || _savingEdit || _selectionStart is not null)
        {
            _localChangePending = true;
            return;
        }
        _localChangePending = false;
        RenderScheduler();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    public Task<bool> RefreshAsync() => LoadRangeAsync();

    internal void InvalidateWorklogs() => _rangeCache.Clear();

    private async Task<bool> LoadRangeAsync(bool navigating = false)
    {
        if (_isClosed) return false;
        if (_calendarEdit is not null || _savingEdit)
        {
            _refreshPending = true;
            if (navigating) RenderScheduler(resetScroll: true);
            return false;
        }
        _refreshPending = false;
        var version = ++_loadVersion;
        var selectedIssue = _issue;
        var range = CurrentRange();
        _load?.Cancel();
        _load = null;
        var hasCachedRange = _rangeCache.TryGetValue(range, out var cached);
        if (hasCachedRange)
        {
            ApplyWorklogs(cached.Result);
            if (navigating && DateTimeOffset.UtcNow - cached.LoadedAt < CacheLifetime &&
                _issues().Select(issue => issue.Key).Append(selectedIssue.Key).Concat(_localIssueKeys)
                    .All(key => cached.Result.IssueKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
            {
                ReportLoaded(cached.Result);
                RefreshButton.IsEnabled = true;
                RenderScheduler(resetScroll: true);
                return true;
            }
            SetLoadWarning("Showing cached worklogs while Jira is checked.");
        }
        else if (navigating)
        {
            _worklogs = [];
            _worklogIssues = [];
            SetLoadWarning(string.Empty);
        }
        if (navigating) RenderScheduler(resetScroll: true);

        var load = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        _load = load;
        RefreshButton.IsEnabled = false;
        StatusText.Text = hasCachedRange ? "Refreshing your Jira worklogs…" : "Loading your Jira worklogs…";
        try
        {
            var result = await WorklogCalendarData.LoadAsync(_client, _issues(), selectedIssue, range,
                load.Token, _localIssueKeys, hasCachedRange ? null : ShowPreview);
            if (!IsCurrent()) return false;
            if (_calendarEdit is not null || _savingEdit)
            {
                _refreshPending = true;
                return false;
            }
            if (!result.DiscoveryFailed && result.FailedIssueCount == 0)
            {
                _rangeCache[range] = (result, DateTimeOffset.UtcNow);
                if (_rangeCache.Count > CachedRangeLimit)
                    _rangeCache.Remove(_rangeCache.MinBy(entry => entry.Value.LoadedAt).Key);
            }
            ApplyWorklogs(result);
            ReportLoaded(result);
            RenderScheduler();
            return true;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (version == _loadVersion && range == CurrentRange())
            {
                StatusText.Text = $"Could not load worklogs: {ex.Message}";
                SetLoadWarning("The calendar could not be refreshed. Worklogs and totals may be incomplete.");
            }
            return false;
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

        bool IsCurrent() => version == _loadVersion && range == CurrentRange() &&
            _issue.Key.Equals(selectedIssue.Key, StringComparison.OrdinalIgnoreCase) && !load.IsCancellationRequested;

        void ShowPreview(WorklogCalendarLoadResult preview)
        {
            if (!IsCurrent() || _calendarEdit is not null || _savingEdit || preview.Worklogs.Count == 0) return;
            ApplyWorklogs(preview);
            StatusText.Text = "Checking for older worklogs that overlap this range…";
            SetLoadWarning("Calendar totals may change while older worklogs are checked.");
            RenderScheduler();
        }
    }

    private void ApplyWorklogs(WorklogCalendarLoadResult result)
    {
        _worklogs = result.Worklogs;
        _worklogIssues = result.DiscoveredIssues;
        _accountId = result.AccountId;
    }

    private void ReportLoaded(WorklogCalendarLoadResult result)
    {
        StatusText.Text = $"Loaded your Jira worklogs from {result.IssueKeys.Count - result.FailedIssueCount} ticket(s).";
        SetLoadWarning((result.DiscoveryFailed ? "Jira search did not finish. Some tickets may be missing. " : string.Empty) +
            (result.FailedIssueCount == 0 ? string.Empty : $"Worklogs from {result.FailedIssueCount} ticket(s) may be incomplete."));
    }

    private void SetLoadWarning(string warning)
    {
        LoadWarningText.Text = warning.TrimEnd();
        LoadWarningText.Visibility = warning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ViewMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ViewModeBox.SelectedIndex < 0) return;
        _view = (WorklogCalendarView)ViewModeBox.SelectedIndex;
        MoveToVisibleDay(1);
        await ShowRangeAsync();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        _anchor = WorklogCalendarLayout.Move(_anchor, _view, -1);
        MoveToVisibleDay(-1);
        await ShowRangeAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        _anchor = WorklogCalendarLayout.Move(_anchor, _view, 1);
        MoveToVisibleDay(1);
        await ShowRangeAsync();
    }

    private async void Today_Click(object sender, RoutedEventArgs e)
    {
        _anchor = DateOnly.FromDateTime(DateTime.Today);
        MoveToVisibleDay(1);
        await ShowRangeAsync();
    }

    private async Task ShowRangeAsync()
    {
        if (IsLoaded) await LoadRangeAsync(navigating: true);
        else RenderScheduler(resetScroll: true);
    }

    private void MoveToVisibleDay(int direction)
    {
        if (_view != WorklogCalendarView.Day || _settings() is not { ShowOnlyCalendarWorkdays: true } settings)
            return;
        var workdays = WorklogCalendarLayout.Workdays(settings.CalendarWorkdays);
        while (!workdays.Contains(_anchor.DayOfWeek)) _anchor = _anchor.AddDays(direction);
    }

    private void Highlight_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingHighlight) return;
        _highlightedIssueKey = HighlightIssueBox.SelectedIndex > 0 ? HighlightIssueBox.SelectedItem as string : null;
        RefreshLocalEntries();
    }

    private DayOfWeek FirstDayOfWeek => _settings()?.CalendarFirstDayOfWeek is { } day && Enum.IsDefined(day)
        ? day : CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    private WorklogCalendarRange CurrentRange() => WorklogCalendarLayout.RangeFor(_view, _anchor, FirstDayOfWeek);

    private IEnumerable<JiraIssue> CalendarTickets() => _issues().Append(_issue).Concat(_worklogIssues)
        .DistinctBy(issue => issue.Key, StringComparer.OrdinalIgnoreCase);

    private void RenderScheduler(bool resetScroll = false)
    {
        if (SchedulerHost is null) return;
        MoveToVisibleDay(1);
        var range = CurrentRange();
        RangeTitle.Text = RangeLabel(range);
        var allEntries = Entries().ToList();
        _localIssueKeys.UnionWith(allEntries.Where(entry => entry.Pending).Select(entry => entry.IssueKey));
        var tickets = CalendarTickets().ToList();
        UpdateHighlightOptions(tickets.Select(issue => issue.Key).Concat(allEntries.Select(entry => entry.IssueKey)));
        var settings = _settings();
        UpdateWorklogColors(tickets, settings?.WorklogTypeColors ?? []);
        var days = Enumerable.Range(0, range.DayCount).Select(range.Start.AddDays).ToArray();
        var start = WorklogCalendarLayout.LocalDateTime(range.Start, TimeOnly.MinValue, TimeZoneInfo.Local);
        var end = WorklogCalendarLayout.LocalDateTime(range.EndExclusive, TimeOnly.MinValue, TimeZoneInfo.Local);
        var rangeEntries = allEntries.Where(entry => entry.Started < end && entry.Ended > start).ToList();
        var entries = rangeEntries.SelectMany(entry => days.Select(day => ClipToDay(entry, day)).OfType<CalendarEntry>())
            .OrderBy(e => e.Started)
            .ToList();
        var entriesByDay = entries.ToLookup(entry => entry.Date);
        RangeTotal.Text = $"Total: {FormatDuration(entries.Sum(entry => entry.Seconds))}";

        var workdays = WorklogCalendarLayout.Workdays(settings?.CalendarWorkdays);
        var workHours = WorklogCalendarHours.FromSettings(settings?.CalendarWorkStartMinute, settings?.CalendarWorkEndMinute);
        var onlyWorkdays = settings?.ShowOnlyCalendarWorkdays == true;
        _timeHours = settings?.ShowOnlyCalendarWorkHours == true ? workHours : WorklogCalendarHours.AllDay;
        _timeDays = onlyWorkdays ? days.Where(day => workdays.Contains(day.DayOfWeek)).ToArray() : days;
        RangeTotal.ToolTip = onlyWorkdays || _timeHours != WorklogCalendarHours.AllDay
            ? "Includes all worklogs in the selected period, including time hidden by the display filters." : null;

        if (resetScroll)
            _verticalOffset = Math.Max(0, workHours.StartMinute - 60 - _timeHours.StartMinute) *
                Math.Max(16, _resolutionMinutes * 1.2) / _resolutionMinutes;
        SchedulerHost.Children.Clear();
        _timeScroll = null;
        _timeGrid = null;
        _timeRange = null;
        _currentTimeLine = null;
        if (_view == WorklogCalendarView.Month)
            RenderMonth(range, entries, workdays, onlyWorkdays);
        else
            RenderTimeGrid(range, entriesByDay, workdays, workHours, settings?.DailyWorklogTargetMinutes);
    }

    private void UpdateHighlightOptions(IEnumerable<string> issueKeys)
    {
        var keys = issueKeys.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var choices = new[] { "Current ticket" }.Concat(keys).ToArray();
        if (HighlightIssueBox.ItemsSource is IEnumerable<string> current && current.SequenceEqual(choices)) return;
        _updatingHighlight = true;
        HighlightIssueBox.ItemsSource = choices;
        HighlightIssueBox.SelectedIndex = Math.Max(0, Array.FindIndex(choices, key =>
            string.Equals(key, _highlightedIssueKey, StringComparison.OrdinalIgnoreCase)));
        if (HighlightIssueBox.SelectedIndex == 0) _highlightedIssueKey = null;
        _updatingHighlight = false;
    }

    private void RenderMonth(WorklogCalendarRange range, IReadOnlyList<CalendarEntry> entries,
        IReadOnlySet<DayOfWeek> workdays, bool onlyWorkdays)
    {
        var firstDay = FirstDayOfWeek;
        var back = (7 + (int)range.Start.DayOfWeek - (int)firstDay) % 7;
        var gridStart = range.Start.AddDays(-back);
        var grid = new Grid();
        var weekdays = Enumerable.Range(0, 7).Select(offset => gridStart.AddDays(offset).DayOfWeek)
            .Where(day => !onlyWorkdays || workdays.Contains(day)).ToArray();
        for (var column = 0; column < weekdays.Length; column++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var row = 0; row < 6; row++) grid.RowDefinitions.Add(new RowDefinition());

        for (var column = 0; column < weekdays.Length; column++)
        {
            var day = weekdays[column];
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
            var column = Array.IndexOf(weekdays, date.DayOfWeek);
            if (column < 0) continue;
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
                Background = DayFill(date, today, workdays),
                Child = panel,
                Tag = date,
            };
            border.MouseLeftButtonDown += MonthDay_Click;
            Grid.SetColumn(border, column);
            Grid.SetRow(border, cell / 7 + 1);
            grid.Children.Add(border);
        }
        SchedulerHost.Children.Add(grid);
    }

    private TextBlock MonthEntry(CalendarEntry entry) => new()
    {
        Text = $"{entry.Started:HH:mm}  {FormatDuration(entry.Seconds)}  {entry.Author}  {entry.Comment}",
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 11,
        Margin = new Thickness(0, 1, 0, 1),
        Foreground = EntryFill(entry),
        FontWeight = IsFocusedIssue(entry.IssueKey) ? FontWeights.SemiBold : FontWeights.Normal,
        ToolTip = EntryTooltip(entry),
    };

    private async void MonthDay_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: DateOnly date }) return;
        var previousRange = CurrentRange();
        _anchor = date;
        if (e.ClickCount >= 2)
        {
            _view = WorklogCalendarView.Day;
            ViewModeBox.SelectedIndex = (int)_view;
        }
        else if (CurrentRange() != previousRange)
            await ShowRangeAsync();
        else RenderScheduler();
    }

    private void RenderTimeGrid(WorklogCalendarRange range, ILookup<DateOnly, CalendarEntry> entries,
        IReadOnlySet<DayOfWeek> workdays, WorklogCalendarHours workHours, int? dailyTargetMinutes)
    {
        var days = _timeDays.Length;
        var firstSlot = FirstTimeSlot;
        var slots = TimeSlotCount;
        var slotHeight = Math.Max(16, _resolutionMinutes * 1.2);
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
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        for (var slot = 0; slot < slots; slot++)
            grid.RowDefinitions.Add(new RowDefinition { Height = TimeRowHeight(firstSlot + slot) });

        var headings = DayStrip(grid, days);
        headings.Height = 36;
        var totals = DayStrip(grid, days);
        totals.Background = Brushes.WhiteSmoke;
        totals.Children.Add(new TextBlock
        {
            Text = "Daily total",
            Foreground = Brushes.Black,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Margin = new Thickness(4, 5, 0, 5),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var renderedDays = new List<(List<RenderedEntry> Entries,
            IReadOnlyList<(int Lane, int LaneCount)> Lanes)>();
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var day = 0; day < days; day++)
        {
            var date = _timeDays[day];
            var header = new Border
            {
                BorderBrush = GridLine,
                BorderThickness = new Thickness(0.5),
                Background = DayFill(date, today, workdays),
                Child = new TextBlock
                {
                    Text = date.ToString(days == 1 ? "dddd, d MMMM" : "ddd, d MMM", CultureInfo.CurrentCulture),
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(header, day + 1);
            headings.Children.Add(header);
            var total = DayTotal(entries[date].Sum(entry => entry.Seconds), dailyTargetMinutes);
            Grid.SetColumn(total, day + 1);
            totals.Children.Add(total);
        }

        for (var slot = 0; slot < slots; slot++)
        {
            var minute = _timeHours.StartOfSlot(firstSlot + slot, _resolutionMinutes);
            var label = new TextBlock
            {
                Text = DateTime.Today.AddMinutes(minute).ToString("t", CultureInfo.CurrentCulture),
                FontSize = 10,
                Opacity = minute % 60 == 0 ? 0.85 : 0.6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, slot == 0 ? 0 : -7, 6, 0),
            };
            Grid.SetRow(label, slot + 1);
            grid.Children.Add(label);
            for (var day = 0; day < days; day++)
            {
                var cell = new Border
                {
                    BorderBrush = GridLine,
                    BorderThickness = new Thickness(0.5, 0, 0.5, 0),
                    Child = new Border
                    {
                        BorderBrush = minute % 60 == 0 ? HourLine :
                            minute % 30 == 0 ? HalfHourLine : ResolutionLine,
                        BorderThickness = new Thickness(0, minute % 60 == 0 ? 2 : 1, 0, 0),
                        IsHitTestVisible = false,
                        SnapsToDevicePixels = true,
                    },
                    Background = DayFill(_timeDays[day], today, workdays),
                };
                Grid.SetColumn(cell, day + 1);
                Grid.SetRow(cell, slot + 1);
                grid.Children.Add(cell);
            }
        }

        for (var day = 0; day < days; day++)
        {
            var date = _timeDays[day];
            var visibleEntries = entries[date];
            if (_timeHours != WorklogCalendarHours.AllDay)
            {
                var intervals = _timeHours.IntervalsForDay(date, TimeZoneInfo.Local);
                visibleEntries = visibleEntries.SelectMany(entry => intervals
                    .Select(interval => ClipEntry(entry, interval.Start, interval.End)).OfType<CalendarEntry>());
            }
            var dayEntries = visibleEntries.Select(RenderEntry).ToList();
            var lanes = WorklogCalendarLayout.AssignOverlapLanes(dayEntries
                .Select(item => (item.StartSlot, item.StartSlot + item.Span)).ToList());
            renderedDays.Add((dayEntries, lanes));
        }

        var overlay = new Grid();
        for (var column = 0; column < days; column++)
            overlay.ColumnDefinitions.Add(new ColumnDefinition());
        for (var slot = 0; slot < slots; slot++)
            overlay.RowDefinitions.Add(new RowDefinition { Height = TimeRowHeight(firstSlot + slot) });
        Grid.SetColumn(overlay, 1);
        Grid.SetColumnSpan(overlay, days);
        Grid.SetRow(overlay, 1);
        Grid.SetRowSpan(overlay, slots);
        Panel.SetZIndex(overlay, 2);
        grid.Children.Add(overlay);

        for (var day = 0; day < renderedDays.Count; day++)
        {
            var renderedDay = renderedDays[day];
            for (var index = 0; index < renderedDay.Entries.Count; index++)
            {
                var rendered = renderedDay.Entries[index];
                var block = WorklogBlock(rendered.Entry);
                var lane = renderedDay.Lanes[index];
                var host = new Grid();
                for (var column = 0; column < lane.LaneCount; column++)
                    host.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(block, lane.Lane);
                host.Children.Add(block);
                Grid.SetColumn(host, day);
                Grid.SetRow(host, rendered.StartSlot);
                Grid.SetRowSpan(host, rendered.Span);
                overlay.Children.Add(host);
            }
        }

        AddWorkHourLines(grid, workdays, workHours, slots);
        _currentTimeLine = new Border
        {
            Height = 2,
            Background = Brushes.OrangeRed,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        System.Windows.Automation.AutomationProperties.SetName(_currentTimeLine, "Current time");
        Grid.SetRow(_currentTimeLine, 1);
        Grid.SetRowSpan(_currentTimeLine, slots);
        Panel.SetZIndex(_currentTimeLine, 3);
        grid.Children.Add(_currentTimeLine);
        UpdateCurrentTime(DateTime.Now);

        _timeScroll = new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // Measure both grids against the viewport width. Horizontal scrolling
            // measures with infinite width, making star columns size to their
            // different contents and misaligning worklog lanes with day columns.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var scroll = _timeScroll;
        scroll.ScrollChanged += (_, e) =>
        {
            if (_timeScroll != scroll) return;
            // A replacement content tree may not have completed layout before the next
            // refresh. Retain the requested offset until the new extent is measured.
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
                scroll.ScrollToVerticalOffset(_verticalOffset);
            else
                _verticalOffset = e.VerticalOffset;
        };
        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headings, Dock.Top);
        DockPanel.SetDock(totals, Dock.Bottom);
        layout.Children.Add(headings);
        layout.Children.Add(totals);
        layout.Children.Add(scroll);
        SchedulerHost.Children.Add(layout);
        scroll.ScrollToVerticalOffset(_verticalOffset);
    }

    private int FirstTimeSlot => _timeHours.FirstSlot(_resolutionMinutes);

    private int TimeSlotCount => _timeHours.EndSlot(_resolutionMinutes) - FirstTimeSlot;

    private RenderedEntry RenderEntry(CalendarEntry entry)
    {
        var absoluteSlot = WorklogCalendarLayout.SlotIndex(TimeOnly.FromDateTime(entry.Started.DateTime), _resolutionMinutes);
        var startSlot = absoluteSlot - FirstTimeSlot;
        var span = WorklogCalendarLayout.DisplaySlotSpan(entry.Started, entry.Seconds, _resolutionMinutes, TimeZoneInfo.Local);
        if (_timeHours != WorklogCalendarHours.AllDay)
        {
            var end = entry.Ended;
            var endMinute = end.Date == entry.Started.Date ? end.TimeOfDay.TotalMinutes : 24 * 60;
            span = Math.Max(span, (int)Math.Ceiling(endMinute / _resolutionMinutes) - absoluteSlot);
        }
        return new RenderedEntry(entry, startSlot, Math.Clamp(span, 1, TimeSlotCount - startSlot));
    }

    private GridLength TimeRowHeight(int slot) => new(
        (_timeHours.EndOfSlot(slot, _resolutionMinutes) - _timeHours.StartOfSlot(slot, _resolutionMinutes)) *
        _slotHeight / _resolutionMinutes);

    private double TimeY(double minute) => (minute - _timeHours.StartMinute) * _slotHeight / _resolutionMinutes;

    private static Brush DayFill(DateOnly date, DateOnly today, IReadOnlySet<DayOfWeek> workdays) =>
        date == today ? TodayFill : workdays.Contains(date.DayOfWeek) ? WorkdayFill : Brushes.Transparent;

    private void AddWorkHourLines(Grid grid, IReadOnlySet<DayOfWeek> workdays, WorklogCalendarHours workHours, int slots)
    {
        foreach (var minute in new[] { workHours.StartMinute, workHours.EndMinute })
        {
            var top = Math.Max(0, Math.Min(TimeY(minute), TimeY(_timeHours.EndMinute) - 1.5));
            for (var day = 0; day < _timeDays.Length; day++)
            {
                if (!workdays.Contains(_timeDays[day].DayOfWeek)) continue;
                var line = new Line
                {
                    X2 = 1,
                    Stretch = Stretch.Fill,
                    Stroke = Brushes.SlateGray,
                    StrokeThickness = 1.5,
                    StrokeDashArray = [4, 2],
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, top, 0, 0),
                    IsHitTestVisible = false,
                };
                Grid.SetColumn(line, day + 1);
                Grid.SetRow(line, 1);
                Grid.SetRowSpan(line, slots);
                Panel.SetZIndex(line, 1);
                grid.Children.Add(line);
            }
        }
    }

    private static Grid DayStrip(Grid body, int days)
    {
        var strip = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        strip.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = body });
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        for (var day = 0; day < days; day++) strip.ColumnDefinitions.Add(new ColumnDefinition());
        return strip;
    }

    private static Border DayTotal(int seconds, int? targetMinutes)
    {
        int? difference = targetMinutes is > 0 and <= 1440 ? seconds - targetMinutes.Value * 60 : null;
        var text = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 5, 2, 5),
        };
        var color = difference switch
        {
            < 0 => Brushes.DarkRed,
            0 => Brushes.DarkGreen,
            > 0 => Brushes.Purple,
            _ => Brushes.Black,
        };
        text.Inlines.Add(new Run(FormatDuration(seconds)) { Foreground = color });
        if (difference is < 0 or > 0)
        {
            var qualifier = difference < 0 ? "left" : "over";
            text.Inlines.Add(new Run($" ({FormatDuration(Math.Abs(difference.Value))} {qualifier})")
                { Foreground = Brushes.Black });
        }
        return new Border
        {
            BorderBrush = GridLine,
            BorderThickness = new Thickness(0.5),
            Child = text,
        };
    }

    private void UpdateCurrentTime(DateTime now)
    {
        if (_currentTimeLine is null || _timeRange is not { } range) return;
        var today = DateOnly.FromDateTime(now);
        var day = Array.IndexOf(_timeDays, today);
        if (today < range.Start || today >= range.EndExclusive || day < 0 || !_timeHours.Contains(now.TimeOfDay.TotalMinutes))
        {
            _currentTimeLine.Visibility = Visibility.Collapsed;
            return;
        }

        Grid.SetColumn(_currentTimeLine, day + 1);
        _currentTimeLine.Margin = new Thickness(0, TimeY(now.TimeOfDay.TotalMinutes), 0, 0);
        _currentTimeLine.Visibility = Visibility.Visible;
    }

    private Border WorklogBlock(CalendarEntry entry)
    {
        var fill = EntryFill(entry);
        var canDrag = !entry.Pending && !entry.Segment;
        var text = new TextBlock
        {
            Text = $"{entry.Started:HH:mm}–{entry.Ended:HH:mm}  {FormatDuration(entry.Seconds)}\n" +
                   $"{entry.IssueKey} · {entry.Author}  {entry.Comment}",
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = WorklogTextBrush(fill.Color),
            FontSize = 11,
            Margin = new Thickness(6, 3, 6, 3),
        };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(canDrag ? 7 : 0) });
        content.Children.Add(text);
        var resizeHandle = new Border
        {
            Height = 5,
            Margin = new Thickness(16, 0, 16, 1),
            CornerRadius = new CornerRadius(2),
            Background = MakeBrush(0xB0, 0xFF, 0xFF, 0xFF),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNS,
        };
        Grid.SetRow(resizeHandle, 1);
        if (!canDrag) resizeHandle.Visibility = Visibility.Collapsed;
        content.Children.Add(resizeHandle);

        var block = new Border
        {
            Margin = new Thickness(2, 1, 2, 1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Background = fill,
            BorderBrush = IsFocusedIssue(entry.IssueKey) ? Brushes.DodgerBlue : Brushes.DimGray,
            BorderThickness = new Thickness(IsFocusedIssue(entry.IssueKey) ? 2 : 1),
            ToolTip = EntryTooltip(entry) + (entry.Pending ? "\nThis worklog can be edited after it is saved in Jira."
                : entry.Segment ? "\nClick to edit the full worklog."
                : "\nClick to edit; drag to move; drag the bottom edge to change duration."),
            Child = content,
            Tag = entry,
            Cursor = entry.Pending ? Cursors.Arrow : canDrag ? Cursors.SizeAll : Cursors.Hand,
        };
        if (entry.Pending)
            block.MouseLeftButtonDown += (_, e) => e.Handled = true;
        else
        {
            block.PreviewMouseLeftButtonDown += Worklog_MouseLeftButtonDown;
            block.PreviewMouseMove += Worklog_MouseMove;
            block.PreviewMouseLeftButtonUp += Worklog_MouseLeftButtonUp;
            block.LostMouseCapture += Worklog_LostMouseCapture;
        }
        return block;
    }

    private void Worklog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_savingEdit || sender is not Border { Tag: CalendarEntry entry } block || _timeGrid is null) return;
        var resize = !entry.Segment && e.GetPosition(block).Y >= Math.Max(0, block.ActualHeight - 5);
        var row = Grid.GetRow((Grid)block.Parent);
        var span = Grid.GetRowSpan((Grid)block.Parent);
        var pointer = TimeSlotAt(_timeGrid, e.GetPosition(_timeGrid));
        _calendarEdit = new CalendarEdit(entry, resize ? EditMode.Resize : EditMode.Move, block,
            Array.IndexOf(_timeDays, entry.Date), row, span,
            pointer is null ? 0 : Math.Clamp(pointer.Slot - row, 0, Math.Max(0, span - 1)),
            e.GetPosition(_timeGrid));
        block.CaptureMouse();
        StatusText.Text = entry.Segment ? "Click to edit the full worklog…" :
            resize ? "Drag the bottom edge to change duration…" : "Drag the worklog to a new time or day…";
        e.Handled = true;
    }

    private void Worklog_MouseMove(object sender, MouseEventArgs e)
    {
        if (_calendarEdit is not { } edit || edit.Entry.Segment || sender != edit.Block || e.LeftButton != MouseButtonState.Pressed ||
            _timeGrid is null || TimeSlotAt(_timeGrid, e.GetPosition(_timeGrid)) is not { } slot) return;
        var position = e.GetPosition(_timeGrid);
        if (!edit.Dragged && Math.Abs(position.X - edit.PointerStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - edit.PointerStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        edit.Dragged = true;
        var host = (Grid)edit.Block.Parent;
        if (edit.Mode == EditMode.Move)
            PreviewWorklogMove(edit, slot, host);
        else
        {
            var span = Math.Clamp(slot.Slot - edit.OriginalSlot + 1, 1, TimeSlotCount - edit.OriginalSlot);
            Grid.SetRowSpan(host, span);
        }
        e.Handled = true;
    }

    private void PreviewWorklogMove(CalendarEdit edit, TimeSlot slot, Grid host)
    {
        var day = Array.IndexOf(_timeDays, slot.Date);
        var row = Math.Max(0, slot.Slot - edit.GrabOffset);
        var span = edit.OriginalSpan;
        if (_timeHours == WorklogCalendarHours.AllDay)
        {
            row = Math.Min(row, Math.Max(0, TimeSlotCount - span));
            edit.MoveStarted = WorklogCalendarHours.Boundary(slot.Date, row * _resolutionMinutes, TimeZoneInfo.Local);
        }
        else
        {
            edit.MoveStarted = _timeHours.FindMoveStart(slot.Date, FirstTimeSlot + row, edit.Entry.Seconds,
                _resolutionMinutes, TimeZoneInfo.Local);
            if (edit.MoveStarted is { } started)
            {
                var preview = RenderEntry(edit.Entry with { Started = started });
                row = preview.StartSlot;
                span = preview.Span;
            }
            else
            {
                day = edit.OriginalDay;
                row = edit.OriginalSlot;
            }
        }
        edit.CurrentDay = day;
        Grid.SetColumn(host, day);
        Grid.SetRow(host, row);
        Grid.SetRowSpan(host, span);
    }

    private async void Worklog_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_calendarEdit is not { } edit || sender != edit.Block || _timeRange is null) return;
        _calendarEdit = null;
        edit.Block.ReleaseMouseCapture();
        var day = edit.CurrentDay;
        e.Handled = true;
        var slot = Grid.GetRow((Grid)edit.Block.Parent);
        var span = Grid.GetRowSpan((Grid)edit.Block.Parent);
        if (!edit.Dragged)
        {
            await ShowEditWorklogAsync(edit.Entry);
            FinishCalendarEdit();
            return;
        }
        if (day == edit.OriginalDay && slot == edit.OriginalSlot && span == edit.OriginalSpan)
        {
            StatusText.Text = $"{_worklogs.Count} Jira worklog(s) loaded";
            RenderScheduler();
            FinishCalendarEdit();
            return;
        }

        var date = _timeDays[day];
        var started = edit.MoveStarted ?? edit.Entry.Started;
        var seconds = edit.Mode == EditMode.Move
            ? edit.Entry.Seconds
            : ResizeSeconds(edit.Entry.Started, date, FirstTimeSlot + slot + span - 1);
        await SaveCalendarEditAsync(edit.Entry, started, seconds);
        e.Handled = true;
    }

    private void Worklog_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_calendarEdit is null) return;
        _calendarEdit = null;
        StatusText.Text = "Edit cancelled.";
        RenderScheduler();
        FinishCalendarEdit();
    }

    private async Task SaveCalendarEditAsync(CalendarEntry entry, DateTimeOffset started, int seconds)
    {
        _savingEdit = true;
        var issueKey = entry.IssueKey;
        StatusText.Text = "Checking the latest Jira worklog…";
        try
        {
            var latest = await _client.GetWorklogsAsync(issueKey, _closed.Token);
            var current = latest.FirstOrDefault(w => w.Id == entry.Id);
            if (current is null || current.Started.ToUniversalTime() != entry.Started.ToUniversalTime() ||
                current.TimeSpentSeconds != entry.Seconds || !string.Equals(current.Comment ?? string.Empty,
                    entry.Comment, StringComparison.Ordinal))
            {
                InvalidateWorklogs();
                _worklogs = WorklogCalendarData.ReplaceIssueWorklogs(_worklogs, issueKey,
                    WorklogCalendarData.ForAccount(latest, _accountId));
                StatusText.Text = "This worklog changed in Jira. The calendar was refreshed without overwriting it.";
                RenderScheduler();
                return;
            }

            StatusText.Text = "Saving the changed worklog to Jira…";
            await _client.UpdateWorklogAsync(issueKey, current, started, seconds, _closed.Token);
            InvalidateWorklogs();
            var updated = current with { Started = started, TimeSpentSeconds = seconds };
            var updatedIssue = latest.Select(worklog => worklog.Id == updated.Id ? updated : worklog).ToList();
            _worklogs = WorklogCalendarData.ReplaceIssueWorklogs(_worklogs, issueKey,
                WorklogCalendarData.ForAccount(updatedIssue, _accountId));
            RenderScheduler();
            _savingEdit = false;
            if (ApplyPendingFocus()) return;
            if (await RefreshAsync()) StatusText.Text = "Worklog updated in Jira.";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not update the worklog; its previous position was restored: {ex.Message}";
            RenderScheduler();
        }
        finally
        {
            _savingEdit = false;
            ApplyPendingFocus();
            if (_refreshPending) _ = RefreshAsync();
        }
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
        await ShowAddWorklogAsync(start.Date, FirstTimeSlot + first, count, _issue, _timeHours);
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
        var day = Array.IndexOf(_timeDays, start.Date);
        Grid.SetColumn(_selectionBlock, day + 1);
        Grid.SetRow(_selectionBlock, first + 1);
        Grid.SetRowSpan(_selectionBlock, count);
    }

    private void ClearSelection(Grid grid)
    {
        var block = _selectionBlock;
        _selectionStart = null;
        _selectionBlock = null;
        if (grid.IsMouseCaptured) grid.ReleaseMouseCapture();
        if (block is not null) grid.Children.Remove(block);
    }

    private TimeSlot? TimeSlotAt(Grid grid, Point point)
    {
        if (_timeRange is null || point.X < 64 || point.Y < 0 || grid.ActualWidth <= 64) return null;
        var days = _timeDays.Length;
        var dayWidth = (grid.ActualWidth - 64) / days;
        var day = Math.Clamp((int)((point.X - 64) / dayWidth), 0, days - 1);
        var minute = _timeHours.StartMinute + point.Y * _resolutionMinutes / _slotHeight;
        var slot = Math.Clamp((int)(minute / _resolutionMinutes) - FirstTimeSlot, 0, TimeSlotCount - 1);
        return new TimeSlot(_timeDays[day], slot);
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
        await ShowAddWorklogAsync(slot.Date, FirstTimeSlot + slot.Slot, 1, issue, _timeHours);
        e.Handled = true;
    }

    private async void NewWorklog_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        var slot = WorklogCalendarLayout.SlotIndex(TimeOnly.FromDateTime(now), _resolutionMinutes);
        await ShowAddWorklogAsync(_anchor, slot, 1, _issue, WorklogCalendarHours.AllDay);
    }

    private async Task ShowAddWorklogAsync(DateOnly date, int startSlot, int slotCount, JiraIssue selected,
        WorklogCalendarHours hours)
    {
        var started = WorklogCalendarHours.Boundary(date, hours.StartOfSlot(startSlot, _resolutionMinutes), TimeZoneInfo.Local);
        var end = WorklogCalendarHours.Boundary(date, hours.EndOfSlot(startSlot + slotCount - 1, _resolutionMinutes), TimeZoneInfo.Local);
        var seconds = Math.Max(60, (int)Math.Round((end - started).TotalSeconds));
        var dialog = new AddWorklogWindow(_issues(), selected, started,
            TimeSpan.FromSeconds(seconds), _client)
        {
            Owner = this,
        };
        dialog.ShowDialog();
        if (!dialog.WorklogSaved || dialog.SavedIssue is not { } saved) return;
        InvalidateWorklogs();
        if (!_issue.Key.Equals(saved.Key, StringComparison.OrdinalIgnoreCase))
            FocusIssue(saved);
        else
            await RefreshAsync();
    }

    private async Task ShowEditWorklogAsync(CalendarEntry entry)
    {
        var worklog = _worklogs.FirstOrDefault(w => w.Id == entry.Id && w.IssueKey == entry.IssueKey);
        if (worklog is null) return;
        var issue = CalendarTickets().FirstOrDefault(i => i.Key.Equals(entry.IssueKey, StringComparison.OrdinalIgnoreCase))
            ?? new JiraIssue(entry.IssueKey, entry.IssueKey, null, "", "", "");
        _savingEdit = true;
        var saved = false;
        try
        {
            var dialog = new AddWorklogWindow([issue], issue, worklog.Started,
                TimeSpan.FromSeconds(worklog.TimeSpentSeconds), _client, worklog) { Owner = this };
            dialog.ShowDialog();
            saved = dialog.WorklogSaved || dialog.WorklogDeleted;
        }
        finally { _savingEdit = false; }
        StatusText.Text = $"{_worklogs.Count} Jira worklog(s) loaded";
        if (saved)
        {
            InvalidateWorklogs();
            await RefreshAsync();
        }
    }

    private string EntryTooltip(CalendarEntry entry) =>
        $"{entry.IssueKey} · {entry.Author}\n{entry.Started:dddd, d MMMM yyyy HH:mm}–{entry.Ended:HH:mm}\n" +
        $"{FormatDuration(entry.Seconds)} · {(entry.Running ? "Running timer" : entry.Pending ? "Pending locally" : "Saved in Jira")}" +
        (string.IsNullOrWhiteSpace(entry.Comment) ? string.Empty : $"\n{entry.Comment}");

    private IEnumerable<CalendarEntry> Entries()
    {
        foreach (var worklog in _worklogs)
            yield return new(worklog.Id, worklog.IssueKey, worklog.Started.ToLocalTime(), worklog.TimeSpentSeconds,
                worklog.Author, worklog.Comment ?? string.Empty, false);
        foreach (var pending in _pending())
            yield return new(pending.Id.ToString("N"), pending.Key, pending.StartedUtc.ToLocalTime(), pending.Seconds,
                "Pending local worklog", pending.LastError ?? string.Empty, true);
        if (_active() is { } active)
        {
            var elapsed = (DateTimeOffset.UtcNow - active.StartedUtc).TotalSeconds;
            if (elapsed >= 0 && elapsed <= int.MaxValue)
                yield return new("running", active.Key, active.StartedUtc.ToLocalTime(), Math.Max(1, (int)elapsed),
                    "Running timer", string.Empty, true, Running: true);
        }
    }

    private static CalendarEntry? ClipToDay(CalendarEntry entry, DateOnly day)
    {
        if (entry.Date > day) return null;
        var entryEnd = entry.Ended;
        if (entryEnd.Date < day.ToDateTime(TimeOnly.MinValue)) return null;
        var midnight = WorklogCalendarLayout.LocalDateTime(day, TimeOnly.MinValue, TimeZoneInfo.Local);
        var nextMidnight = WorklogCalendarLayout.LocalDateTime(day.AddDays(1), TimeOnly.MinValue, TimeZoneInfo.Local);
        return ClipEntry(entry, midnight, nextMidnight);
    }

    private static CalendarEntry? ClipEntry(CalendarEntry entry, DateTimeOffset from, DateTimeOffset until)
    {
        var entryEnd = entry.Ended;
        var start = entry.Started > from ? entry.Started : from;
        var end = entryEnd < until ? entryEnd : until;
        if (end <= start) return null;
        if (start == entry.Started && end == entryEnd) return entry;
        return entry with { Started = start.ToLocalTime(), Seconds = (int)(end - start).TotalSeconds, Segment = true };
    }

    private void UpdateWorklogColors(IReadOnlyList<JiraIssue> tickets, IEnumerable<WorklogTypeColor> rules)
    {
        var byType = rules.Where(rule => rule?.Color is ['#', _, _, _, _, _, _])
            .ToLookup(rule => $"{rule.Project}/{rule.IssueType}", StringComparer.OrdinalIgnoreCase);
        _worklogColors.Clear();
        foreach (var issue in tickets)
        {
            var separator = issue.Key.LastIndexOf('-');
            if (separator < 1 || byType[$"{issue.Key[..separator]}/{issue.IssueType}"].FirstOrDefault() is not { } rule)
                continue;
            if (uint.TryParse(rule.Color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                _worklogColors[issue.Key] = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
    }

    private SolidColorBrush EntryFill(CalendarEntry entry)
    {
        var color = _worklogColors.GetValueOrDefault(entry.IssueKey, DefaultWorklogColor);
        return entry.Pending
            ? MakeBrush(255, Lighten(color.R), Lighten(color.G), Lighten(color.B))
            : MakeBrush(255, color.R, color.G, color.B);
    }

    private static byte Lighten(byte channel) => (byte)(channel + (255 - channel) * 0.45);

    internal static Brush WorklogTextBrush(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        var luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? Brushes.Black : Brushes.White;
    }

    private bool IsFocusedIssue(string issueKey) =>
        issueKey.Equals(_highlightedIssueKey ?? _issue.Key, StringComparison.OrdinalIgnoreCase);

    private int ResizeSeconds(DateTimeOffset started, DateOnly date, int endSlot)
    {
        var end = WorklogCalendarHours.Boundary(date, _timeHours.EndOfSlot(endSlot, _resolutionMinutes), TimeZoneInfo.Local);
        return Math.Max(60, (int)Math.Round((end.ToUniversalTime() - started.ToUniversalTime()).TotalSeconds));
    }

    private bool ApplyPendingFocus()
    {
        if (_pendingFocusIssue is not { } pending) return false;
        _pendingFocusIssue = null;
        ApplyFocusIssue(pending);
        return true;
    }

    private void FinishCalendarEdit()
    {
        ApplyPendingFocus();
        if (_refreshPending) _ = RefreshAsync();
    }

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
        _currentTimeTimer.Stop();
        _currentTimeTimer.Tick -= CurrentTimeTimer_Tick;
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

    private sealed record CalendarEntry(string Id, string IssueKey, DateTimeOffset Started, int Seconds,
        string Author, string Comment, bool Pending, bool Running = false, bool Segment = false)
    {
        public DateOnly Date => DateOnly.FromDateTime(Started.DateTime);
        public DateTimeOffset Ended => Started.AddSeconds(Seconds).ToLocalTime();
    }

    private sealed record RenderedEntry(CalendarEntry Entry, int StartSlot, int Span);

    private sealed record TimeSlot(DateOnly Date, int Slot);

    private enum EditMode { Move, Resize }

    private sealed class CalendarEdit(CalendarEntry entry, EditMode mode, Border block,
        int originalDay, int originalSlot, int originalSpan, int grabOffset, Point pointerStart)
    {
        public CalendarEntry Entry { get; } = entry;
        public EditMode Mode { get; } = mode;
        public Border Block { get; } = block;
        public int OriginalDay { get; } = originalDay;
        public int OriginalSlot { get; } = originalSlot;
        public int OriginalSpan { get; } = originalSpan;
        public int GrabOffset { get; } = grabOffset;
        public Point PointerStart { get; } = pointerStart;
        public bool Dragged { get; set; }
        public int CurrentDay { get; set; } = originalDay;
        public DateTimeOffset? MoveStarted { get; set; }
    }

    public const string TicketDragFormat = "JiraTaskBar.TicketKey";
}
