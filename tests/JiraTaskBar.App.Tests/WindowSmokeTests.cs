using System.IO;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using JiraTaskBar.App.Interop;
using JiraTaskBar.App.Services;
using JiraTaskBar.App.ViewModels;
using JiraTaskBar.App.Views;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Storage;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.App.Tests;

/// <summary>
/// Loads every window with the real App resources so XAML, styles and templates are
/// validated at test time (they otherwise only fail when a user opens the window).
/// </summary>
public class WindowSmokeTests
{
    [Fact]
    public void Windows_load_with_app_resources()
    {
        RunOnSta(() =>
        {
            AppPaths.DataDirectory = Path.Combine(Path.GetTempPath(), "JiraTaskBarSmoke", Guid.NewGuid().ToString("N"));
            var app = new App();
            app.InitializeComponent();

            var controller = new AppController(app);
            var issue = new JiraIssue("SD-42", "Printer on fire", new JiraPriority("1", "1 - Critical", 1), "Waiting", "service_desk", "Incident");

            // Bar: vertical and horizontal layouts with a ticket rendered through the item template.
            var bar = new BarWindow(controller.Bar, controller);
            controller.Bar.Tickets.Add(new TicketViewModel(issue, _ => { }, _ => { }, _ => { }) { IsActive = true, ElapsedText = "0:42" });
            bar.Show();
            bar.UpdateLayout();
            var monitor = MonitorInfo.FromWindow(new WindowInteropHelper(bar).Handle);
            bar.ApplyDock(DockEdge.Floating, monitor);
            bar.PlaceFloating(null, null, monitor);
            bar.UpdateLayout();
            Assert.True(bar.ActualHeight > 0);
            var indicator = new DueDateIndicator { Width = 16, Height = 16 };
            System.Windows.Automation.AutomationProperties.SetName(indicator, "Due today");
            Assert.Equal("Due today", System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(indicator).GetName());
            var indicatorWindow = new Window { Content = indicator, SizeToContent = SizeToContent.WidthAndHeight };
            indicatorWindow.Show();
            indicatorWindow.UpdateLayout();
            Assert.Same(indicator, indicator.InputHitTest(new Point(8, 8)));
            foreach (var state in new[]
            {
                new DueDateState(DueDateKind.Upcoming, 1, "Full interval"),
                new DueDateState(DueDateKind.Upcoming, 0.75, "Large remaining sector"),
                new DueDateState(DueDateKind.Soon, 0.25, "Small remaining sector"),
                new DueDateState(DueDateKind.Today, 0.1, "Due today"),
                new DueDateState(DueDateKind.Overdue, 0, "Overdue"),
                new DueDateState(DueDateKind.Upcoming, null, "Unknown baseline"),
                new DueDateState(DueDateKind.None, null, "No due date"),
            })
            {
                indicator.State = state;
                indicatorWindow.UpdateLayout();
                var rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rendered.Render(indicator);
                if (state.Kind == DueDateKind.Today)
                {
                    var dueToday = new DueDateIndicator { Width = 16, Height = 16, State = state };
                    dueToday.Measure(new Size(16, 16));
                    dueToday.Arrange(new Rect(0, 0, 16, 16));
                    rendered = new System.Windows.Media.Imaging.RenderTargetBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rendered.Render(dueToday);
                    var pixels = new byte[16 * 16 * 4];
                    rendered.CopyPixels(pixels, 16 * 4, 0);
                    var redPixels = 0;
                    var whiteOverlayPixels = 0;
                    for (var y = 0; y < 16; y++)
                    for (var x = 0; x < 16; x++)
                    {
                        var pixel = (y * 16 + x) * 4;
                        if (pixels[pixel + 3] > 100 && pixels[pixel + 2] > 100 &&
                            pixels[pixel + 2] > pixels[pixel + 1] * 2) redPixels++;
                        if (x is >= 4 and <= 11 && y is >= 3 and <= 12 && pixels[pixel + 3] > 240 &&
                            pixels[pixel] > 240 && pixels[pixel + 1] > 240 && pixels[pixel + 2] > 240) whiteOverlayPixels++;
                    }
                    Assert.True(redPixels > 0 && whiteOverlayPixels == 0,
                        $"The due-today indicator should be a red pie without a white letter overlay. " +
                        $"Found {redPixels} red pixels and {whiteOverlayPixels} white overlay pixels.");
                }
                Assert.Same(indicator, indicator.InputHitTest(new Point(8, 8)));
            }
            indicatorWindow.Close();
            typeof(BarViewModel).GetField("_lastIssues", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(controller.Bar, new[] { issue });
            controller.Tracker.Start(issue.Key);
            controller.Bar.ApplyFilter(new TicketFilter { Summary = "no matching summary" });
            Assert.Equal(issue.Key, Assert.Single(controller.Bar.Tickets).Key);
            Assert.True(controller.Bar.Tickets[0].IsStale);
            Assert.Equal(issue.Key, controller.Tracker.ActiveKey);
            controller.Tracker.Stop();
            Assert.Empty(controller.Bar.Tickets);
            Assert.Contains(controller.Bar.CalendarIssues, i => i.Key == issue.Key);
            controller.Bar.ApplyFilter(new TicketFilter());
            Assert.Single(controller.Bar.Tickets);
            var filterWindow = new TicketFilterWindow(new TicketFilter(), ["Task", "Bug"]);
            filterWindow.Show();
            filterWindow.UpdateLayout();
            filterWindow.Close();
            var menu = controller.BuildMenu();
            Assert.NotEmpty(menu.Items);

            // Privacy options: capture exclusion applies without error, and keys-only hides the title line.
            CapturePrivacy.Apply(bar, enabled: true);
            Assert.True(CapturePrivacy.Enabled);
            CapturePrivacy.Apply(bar, enabled: false);
            var keysOnly = new TicketViewModel(issue, _ => { }, _ => { }, _ => { }, showTitles: () => false);
            Assert.False(keysOnly.HasSubtitle);
            keysOnly.IsActive = true;
            keysOnly.ElapsedText = "0:05";
            Assert.True(keysOnly.HasSubtitle);

            // Issue type: glyph until Jira's icon loads, then the icon.
            Assert.True(keysOnly.ShowTypeGlyph);
            keysOnly.TypeIcon = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);
            Assert.True(keysOnly.HasTypeIcon);
            Assert.False(keysOnly.ShowTypeGlyph);

            var comment = new CommentWindow(issue, new NullJiraClient());
            comment.ShowNextTo(new WindowInteropHelper(bar).Handle);
            comment.UpdateLayout();
            comment.Close();

            var contextReport = new ContextReportWindow(controller.ContextHistory);
            contextReport.Show();
            contextReport.UpdateLayout();
            contextReport.Close();

            foreach (var resolution in WorklogCalendarLayout.SupportedResolutions)
            {
                var worklogCalendar = new WorklogCalendarWindow(issue, new NullJiraClient(), () => [],
                    resolutionMinutes: resolution);
                worklogCalendar.Show();
                worklogCalendar.UpdateLayout();
                VerifyCurrentTimeIndicator(worklogCalendar, resolution);
                worklogCalendar.Close();
                Assert.False(CalendarField<DispatcherTimer>(worklogCalendar, "_currentTimeTimer").IsEnabled);
            }
            AssertCalendarWorklogsStayInDayColumns(issue);

            var addWorklog = new AddWorklogWindow([issue], issue, DateTimeOffset.Now,
                TimeSpan.FromMinutes(30), new NullJiraClient());
            addWorklog.Show();
            addWorklog.UpdateLayout();
            addWorklog.Close();

            // Regression: Cancel with unsent text used to throw "Cannot set Visibility ... while a
            // Window is closing". The close is now cancelled and the discard prompt deferred.
            var unsent = new CommentWindow(issue, new NullJiraClient());
            unsent.Show();
            ((System.Windows.Controls.TextBox)unsent.FindName("Editor")).Text = "not posted yet";
            unsent.Close();
            Assert.True(unsent.IsVisible);
            unsent.ForceClose();
            Assert.False(unsent.IsVisible);

            // Same for Alt+F4 on the bar: it must not throw, and it hides after the close is cancelled.
            bar.Close();
            Assert.True(bar.IsVisible);

            var settings = new SettingsWindow(new AppSettings(),
                new StoredCredentials("https://example.atlassian.net/", "a@b.c", "t"), firstRun: false,
                message: "Jira rejected the saved API token.");
            settings.Show();
            settings.UpdateLayout();
            settings.Close();

            typeof(AppController).GetProperty(nameof(AppController.IsExiting))!.SetValue(controller, true);
            bar.Close();
            controller.Dispose();
            app.Shutdown();
        });
    }

    private static void AssertCalendarWorklogsStayInDayColumns(JiraIssue issue)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstDay = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var start = today.AddDays(-(7 + (int)today.DayOfWeek - (int)firstDay) % 7);
        var pending = Enumerable.Range(0, 7).SelectMany(day => Enumerable.Range(0, day % 2 + 1)
            .Select(lane => new PendingWorklog(Guid.NewGuid(), issue.Key,
                new DateTimeOffset(start.AddDays(day).ToDateTime(new TimeOnly(9, lane * 10))).ToUniversalTime(),
                3600, new string('W', 200), 0))).ToList();
        var calendar = new WorklogCalendarWindow(issue, new NullJiraClient(), () => pending);
        try
        {
            calendar.Show();
            foreach (var mode in new[] { 1, 2 }) // Week and Day use the same time grid.
            {
                ((ComboBox)calendar.FindName("ViewModeBox")).SelectedIndex = mode;
                foreach (var width in new[] { 760d, 1120d, 1600d, 760d })
                {
                    calendar.Width = width;
                    calendar.UpdateLayout();
                    var host = (Grid)calendar.FindName("SchedulerHost");
                    var scroll = Assert.IsType<ScrollViewer>(Assert.Single(host.Children.Cast<UIElement>()));
                    var grid = Assert.IsType<Grid>(scroll.Content);
                    var overlay = Assert.Single(grid.Children.OfType<Grid>());
                    var days = grid.ColumnDefinitions.Count - 1;
                    var lanes = overlay.ColumnDefinitions.Count / days;
                    Assert.True(grid.ActualWidth <= scroll.ViewportWidth + 0.1,
                        "Long worklog text must not expand the calendar beyond the viewport.");
                    Assert.NotEmpty(overlay.Children.Cast<UIElement>());
                    foreach (var block in overlay.Children.OfType<Border>())
                    {
                        var day = Grid.GetColumn(block) / lanes;
                        var column = grid.ColumnDefinitions[day + 1];
                        var left = block.TranslatePoint(new Point(0, 0), grid).X;
                        Assert.True(left >= column.Offset - 0.1 && left + block.ActualWidth <= column.Offset + column.ActualWidth + 0.1,
                            $"Worklog bounds {left}–{left + block.ActualWidth} exceed day {day} bounds " +
                            $"{column.Offset}–{column.Offset + column.ActualWidth} at width {width} in mode {mode}.");
                    }
                }
            }
        }
        finally { calendar.Close(); }
    }

    private static T CalendarField<T>(WorklogCalendarWindow window, string name) =>
        (T)typeof(WorklogCalendarWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static void VerifyCurrentTimeIndicator(WorklogCalendarWindow window, int resolution)
    {
        var update = typeof(WorklogCalendarWindow).GetMethod("UpdateCurrentTime",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        void SetTime(DateTime time) => update.Invoke(window, [time]);

        Assert.True(CalendarField<DispatcherTimer>(window, "_currentTimeTimer").IsEnabled);
        var range = CalendarField<WorklogCalendarRange>(window, "_timeRange");
        var line = CalendarField<Border>(window, "_currentTimeLine");
        var now = range.Start.AddDays(2).ToDateTime(new TimeOnly(10, 17, 30));
        SetTime(now);
        Assert.Equal(Visibility.Visible, line.Visibility);
        Assert.Equal(3, Grid.GetColumn(line));
        Assert.Equal(1, Grid.GetRow(line));
        Assert.Equal(24 * 60 / resolution, Grid.GetRowSpan(line));
        Assert.Equal(now.TimeOfDay.TotalMinutes / resolution * Math.Max(8, resolution * 1.2),
            line.Margin.Top, 6);
        Assert.Equal(2, line.Height);
        Assert.False(line.IsHitTestVisible);
        Assert.True(Panel.GetZIndex(line) > 2); // Above saved worklogs.

        SetTime(now.AddMinutes(1));
        Assert.True(line.Margin.Top > now.TimeOfDay.TotalMinutes / resolution * Math.Max(8, resolution * 1.2));
        SetTime(now.Date.AddDays(1)); // Midnight moves the line to the next day.
        Assert.Equal(4, Grid.GetColumn(line));
        Assert.Equal(0, line.Margin.Top);
        SetTime(range.Start.AddDays(-1).ToDateTime(TimeOnly.MinValue));
        Assert.Equal(Visibility.Collapsed, line.Visibility);
        SetTime(range.EndExclusive.ToDateTime(TimeOnly.MinValue));
        Assert.Equal(Visibility.Collapsed, line.Visibility);

        var viewMode = (ComboBox)window.FindName("ViewModeBox");
        viewMode.SelectedIndex = (int)WorklogCalendarView.Day;
        range = CalendarField<WorklogCalendarRange>(window, "_timeRange");
        line = CalendarField<Border>(window, "_currentTimeLine");
        SetTime(range.Start.ToDateTime(new TimeOnly(23, 59)));
        Assert.Equal(Visibility.Visible, line.Visibility);
        Assert.Equal(1, Grid.GetColumn(line));
        SetTime(range.EndExclusive.ToDateTime(TimeOnly.MinValue));
        Assert.Equal(Visibility.Collapsed, line.Visibility);

        viewMode.SelectedIndex = (int)WorklogCalendarView.Month;
        Assert.Null(CalendarField<Border?>(window, "_currentTimeLine"));
        SetTime(now); // Timer ticks are harmless in Month view.
        viewMode.SelectedIndex = (int)WorklogCalendarView.Week;
        line = CalendarField<Border>(window, "_currentTimeLine");
        SetTime(now);
        Assert.Equal(Visibility.Visible, line.Visibility);
        Assert.Equal(3, Grid.GetColumn(line));
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    private sealed class NullJiraClient : IJiraClient
    {
        public JiraConnection Connection { get; } = new(new Uri("https://example.atlassian.net/"), "a@b.c", "t");
        public Task<JiraUser> GetMyselfAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JiraDurationSettings> GetTimeTrackingConfigurationAsync(CancellationToken ct = default) =>
            Task.FromResult(JiraDurationSettings.Default);
        public Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraWorklog>> GetWorklogsAsync(string issueKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, string? comment = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateWorklogAsync(string issueKey, JiraWorklog worklog, DateTimeOffset startedLocal, int timeSpentSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> GetIssueTypeIconAsync(string avatarId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
