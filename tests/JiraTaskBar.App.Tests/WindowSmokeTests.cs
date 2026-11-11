using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using JiraTaskBar.App.Interop;
using JiraTaskBar.App.Services;
using JiraTaskBar.App.ViewModels;
using JiraTaskBar.App.Views;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Storage;

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
            var app = new TestApp();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.Add("BoolToVisibility", new System.Windows.Controls.BooleanToVisibilityConverter());
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

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

            var worklogCalendar = new WorklogCalendarWindow(issue, new NullJiraClient(), () => []);
            worklogCalendar.Show();
            worklogCalendar.UpdateLayout();
            worklogCalendar.Close();
            VerifyCalendarWorklogEditing(issue);

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

    private static void VerifyCalendarWorklogEditing(JiraIssue issue)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var original = new JiraWorklog("123", issue.Key, new DateTimeOffset(DateTime.Today.AddHours(9)), 1800, "Me", "Existing description");
        var client = new NullJiraClient { Worklogs = [original] };
        var calendar = new WorklogCalendarWindow(issue, client, () => []);
        calendar.Show();
        calendar.UpdateLayout();

        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (var descendant in Descendants<T>(child)) yield return descendant;
            }
        }

        System.Windows.Controls.Border Block() => Descendants<System.Windows.Controls.Border>(calendar)
            .Single(b => b.Tag?.GetType().Name == "CalendarEntry");

        void Click(string handler, System.Windows.Controls.Border block, int count)
        {
            var args = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = handler.EndsWith("Down", StringComparison.Ordinal)
                    ? UIElement.PreviewMouseLeftButtonDownEvent : UIElement.PreviewMouseLeftButtonUpEvent,
            };
            typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(args, count);
            Assert.Equal(count, args.ClickCount);
            Assert.NotNull(typeof(WorklogCalendarWindow).GetField("_timeGrid", flags)!.GetValue(calendar));
            block.RaiseEvent(args);
        }

        // A plain click must keep the visual block alive for the second click.
        var block = Block();
        Click("Worklog_MouseLeftButtonDown", block, 1);
        Click("Worklog_MouseLeftButtonUp", block, 1);
        Assert.Same(block, Block());

        void DoubleClick(Action<EditWorklogWindow> edit)
        {
            ExceptionDispatchInfo? error = null;
            var opened = false;
            calendar.Dispatcher.BeginInvoke(() =>
            {
                var dialog = Assert.Single(calendar.OwnedWindows.OfType<EditWorklogWindow>());
                opened = true;
                try { edit(dialog); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); dialog.Close(); }
            });
            Click("Worklog_MouseLeftButtonDown", Block(), 2);
            calendar.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            error?.Throw();
            Assert.True(opened, "Double-click should open the edit dialog.");
            calendar.UpdateLayout();
        }

        DoubleClick(dialog => dialog.DialogResult = false);
        Assert.True(calendar.IsVisible);
        Assert.False((bool)typeof(WorklogCalendarWindow).GetField("_savingEdit", flags)!.GetValue(calendar)!, "Edit state must clear after cancel.");
        Assert.Equal(0, client.UpdateCount);
        DoubleClick(dialog =>
        {
            Assert.Equal(original.Started, dialog.Started);
            Descendants<System.Windows.Controls.TextBox>(dialog).Single(t => t.Name == "DurationBox").Text = "1:15:00";
            Descendants<System.Windows.Controls.Button>(dialog).Single(b => b.Name == "SaveButton")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        Assert.True(client.UpdateCount == 1, ((System.Windows.Controls.TextBlock)calendar.FindName("StatusText")).Text);
        Assert.Equal(4500, Assert.Single(client.Worklogs).TimeSpentSeconds);
        Assert.Equal(original.Started, client.Worklogs[0].Started);
        Assert.Equal(original.Comment, client.Worklogs[0].Comment);

        // Refuse to overwrite a worklog changed remotely while the editor was open.
        DoubleClick(dialog =>
        {
            client.Worklogs = [client.Worklogs[0] with { TimeSpentSeconds = 600 }];
            Descendants<System.Windows.Controls.TextBox>(dialog).Single(t => t.Name == "DurationBox").Text = "2:00:00";
            Descendants<System.Windows.Controls.Button>(dialog).Single(b => b.Name == "SaveButton")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        Assert.Equal(1, client.UpdateCount);
        Assert.Equal(600, client.Worklogs[0].TimeSpentSeconds);
        calendar.Close();
    }

    // Modal dialogs pump the dispatcher; avoid launching the real tray app/startup flow.
    private sealed class TestApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    private sealed class NullJiraClient : IJiraClient
    {
        public IReadOnlyList<JiraWorklog> Worklogs { get; set; } = [];
        public int UpdateCount { get; private set; }
        public JiraConnection Connection { get; } = new(new Uri("https://example.atlassian.net/"), "a@b.c", "t");
        public Task<JiraUser> GetMyselfAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JiraDurationSettings> GetTimeTrackingConfigurationAsync(CancellationToken ct = default) =>
            Task.FromResult(JiraDurationSettings.Default);
        public Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraWorklog>> GetWorklogsAsync(string issueKey, CancellationToken ct = default) => Task.FromResult(Worklogs);
        public Task AddWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, string? comment = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateWorklogAsync(string issueKey, JiraWorklog worklog, DateTimeOffset startedLocal, int timeSpentSeconds, CancellationToken ct = default)
        {
            UpdateCount++;
            Worklogs = [worklog with { Started = startedLocal, TimeSpentSeconds = timeSpentSeconds }];
            return Task.CompletedTask;
        }
        public Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> GetIssueTypeIconAsync(string avatarId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
