using System.IO;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
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
            var app = new SmokeTestApp();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.Add("BoolToVisibility", new BooleanToVisibilityConverter());
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
            AssertBrowserDropTargets(controller, bar, monitor, issue);
            AssertQueueBrowserKeepsTheLatestSource(issue);
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
                Assert.Same(indicator, indicator.InputHitTest(new Point(8, 8)));
            }
            AssertUrgencyPieColors();
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
            var fromDate = (ContextDatePicker)contextReport.FindName("FromDate");
            var toDate = (ContextDatePicker)contextReport.FindName("ToDate");
            var fromEditor = (DatePickerTextBox)fromDate.Template.FindName("PART_TextBox", fromDate);
            var toEditor = (DatePickerTextBox)toDate.Template.FindName("PART_TextBox", toDate);
            fromDate.SelectedDate = new DateTime(2026, 11, 3);
            toDate.SelectedDate = new DateTime(2026, 11, 9);
            Assert.Equal(@"03\11\2026", fromEditor.Text);
            Assert.Equal(@"09\11\2026", toEditor.Text);
            contextReport.Close();

            var aboutItem = controller.BuildMenu().Items.OfType<MenuItem>().Single(item => Equals(item.Header, "About…"));
            aboutItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var about = Assert.Single(app.Windows.OfType<AboutWindow>());
            Assert.Equal("Jira TaskBar", ((TextBlock)about.FindName("ProductName")).Text);
            Assert.Matches(@"^Version: \d+\.\d+\.\d+", ((TextBlock)about.FindName("ReleaseVersion")).Text);
            Assert.EndsWith(" UTC", ((TextBlock)about.FindName("BuildTime")).Text);
            about.Close();

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
            AssertSyncedOvernightWorklogKeepsItsDaySegment(issue);
            AssertWeeklyTotalsUseDiscoveredWorklogsAndConfiguredStart(issue);
            AssertCalendarFiltersPreserveFullTotals(issue);

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

            var settings = new SettingsWindow(new AppSettings
            {
                AdditionalTicketKeysBySite = new()
                {
                    ["https://example.atlassian.net/"] = ["TEST-1", "TEST-2"],
                    ["https://other.atlassian.net/"] = ["OTHER-9"],
                },
            }, new StoredCredentials("https://example.atlassian.net/", "a@b.c", "t"), firstRun: false,
                message: "Jira rejected the saved API token.");
            settings.Show();
            settings.UpdateLayout();
            var workdays = (WrapPanel)settings.FindName("WorkdaysPanel");
            Assert.Equal(7, workdays.Children.Count);
            Assert.Equal(5, workdays.Children.OfType<CheckBox>().Count(box => box.IsChecked == true));
            Assert.All(workdays.Children.OfType<CheckBox>(), box => Assert.IsType<string>(box.ToolTip));
            foreach (var name in new[] { "SiteBox", "EmailBox", "TokenBox", "CloudIdBox", "JqlBox", "RefreshBox",
                         "ShortSessionBox", "WorkStartBox", "WorkEndBox", "OnlyWorkdaysBox", "OnlyWorkHoursBox", "WorklogColorsBox", "StartupSwitch" })
                Assert.IsType<string>(((FrameworkElement)settings.FindName(name)).ToolTip);
            var tabs = (TabControl)settings.FindName("SettingsTabs");
            Assert.Equal(8, tabs.Items.Count);
            Assert.Same(settings.FindName("JiraTab"), tabs.SelectedItem);
            var additionalTickets = (TextBox)settings.FindName("AdditionalTicketsBox");
            Assert.Equal(string.Join(Environment.NewLine, "TEST-1", "TEST-2"), additionalTickets.Text);
            tabs.SelectedItem = settings.FindName("AddTicketsTab");
            additionalTickets.CaretIndex = additionalTickets.Text.Length;
            var moveUp = (ButtonBase)settings.FindName("MoveTicketLineUp");
            Assert.True(moveUp.IsTabStop);
            moveUp.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(string.Join(Environment.NewLine, "TEST-2", "TEST-1"), additionalTickets.Text);
            Assert.Equal("TEST-2", additionalTickets.SelectedText);
            ((ButtonBase)settings.FindName("MoveTicketLineDown")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(string.Join(Environment.NewLine, "TEST-1", "TEST-2"), additionalTickets.Text);
            var siteBox = (TextBox)settings.FindName("SiteBox");
            siteBox.Text = "other.atlassian.net";
            Assert.Equal("OTHER-9", additionalTickets.Text);
            siteBox.Text = "fresh.atlassian.net";
            Assert.Empty(additionalTickets.Text);
            siteBox.Text = "example.atlassian.net";
            Assert.Equal(string.Join(Environment.NewLine, "TEST-1", "TEST-2"), additionalTickets.Text);
            additionalTickets.Text = "not-a-ticket";
            typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(settings, [settings, new RoutedEventArgs()]);
            Assert.Same(settings.FindName("AddTicketsTab"), tabs.SelectedItem);
            Assert.Contains("Enter a Jira ticket key", ((Wpf.Ui.Controls.InfoBar)settings.FindName("Status")).Message);
            settings.Close();

            AssertAddedTicketsJoinTheQueue(controller, bar, issue);
            AssertFilteredRunningTicketsPreservePinOrder(controller, issue);
            typeof(AppController).GetProperty(nameof(AppController.IsExiting))!.SetValue(controller, true);
            bar.Close();
            controller.Dispose();
            AssertCalendarClickEditsSavedWorklog(issue);
            app.Shutdown();
        });
    }

    private static void AssertUrgencyPieColors()
    {
        byte[] Render(DueDateKind kind, double fraction)
        {
            var indicator = new DueDateIndicator { Width = 16, Height = 16, State = new(kind, fraction, "Urgency") };
            indicator.Measure(new Size(16, 16));
            indicator.Arrange(new Rect(0, 0, 16, 16));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(indicator);
            var pixels = new byte[16 * 16 * 4];
            bitmap.CopyPixels(pixels, 16 * 4, 0);
            return pixels;
        }

        var full = Render(DueDateKind.Upcoming, 1);
        var half = Render(DueDateKind.Upcoming, 0.5);
        var low = Render(DueDateKind.Today, 0.1);
        var overdue = Render(DueDateKind.Overdue, 0);
        var center = (8 * 16 + 8) * 4;
        var filledSector = (3 * 16 + 9) * 4;
        var usedSector = (6 * 16 + 4) * 4;
        var ring = (1 * 16 + 8) * 4;
        Assert.True(full[center + 1] > full[center + 2] && full[center + 3] == 255);
        Assert.True(half[filledSector + 2] > half[filledSector + 1] && half[filledSector + 1] > half[filledSector]);
        Assert.True(half[usedSector + 3] < 100 && low[usedSector + 3] < 100);
        Assert.True(low[filledSector + 2] > low[filledSector + 1] * 2);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, overdue[center..(center + 4)]);
        Assert.True(overdue[ring + 2] > overdue[ring + 1] * 2);
    }

    private static void AssertBrowserDropTargets(AppController controller, BarWindow bar, MonitorInfo monitor, JiraIssue issue)
    {
        var second = new TicketViewModel(issue with { Key = "SD-43" }, _ => { }, _ => { }, _ => { });
        controller.Bar.Tickets.Add(second);
        try
        {
            var items = (ItemsControl)bar.FindName("TicketItems");
            foreach (var edge in new[] { DockEdge.Floating, DockEdge.Left })
            {
                bar.ApplyDock(edge, monitor);
                bar.UpdateLayout();
                var ticket = Assert.IsAssignableFrom<FrameworkElement>(items.ItemContainerGenerator.ContainerFromItem(second));
                var before = new Point(ticket.ActualWidth / 4, ticket.ActualHeight / 4);
                var after = new Point(ticket.ActualWidth * 3 / 4, ticket.ActualHeight * 3 / 4);
                foreach (var (point, expected) in new[] { (before, false), (after, true) })
                {
                    var hit = Assert.IsAssignableFrom<DependencyObject>(ticket.InputHitTest(point));
                    var (anchor, afterAnchor) = bar.GetBrowserDropTarget(hit, element => ticket.TranslatePoint(point, element));
                    Assert.Equal((second.Key, expected), (anchor, afterAnchor));
                    List<string> pins = [issue.Key, second.Key, "SD-44"];
                    Assert.True(TicketPinOrder.Place(pins, "SD-99", anchor, afterAnchor, moveExisting: true));
                    Assert.Equal(expected ? [issue.Key, second.Key, "SD-99", "SD-44"] :
                        new[] { issue.Key, "SD-99", second.Key, "SD-44" }, pins);
                }
            }
            var (emptyAnchor, emptyAfter) = bar.GetBrowserDropTarget(bar, _ => default);
            Assert.Equal((null, false), (emptyAnchor, emptyAfter));
            List<string> emptySpacePins = [issue.Key, second.Key];
            Assert.True(TicketPinOrder.Place(emptySpacePins, "SD-99", emptyAnchor, emptyAfter, moveExisting: true));
            Assert.Equal([issue.Key, second.Key, "SD-99"], emptySpacePins);
        }
        finally
        {
            controller.Bar.Tickets.Remove(second);
            bar.ApplyDock(DockEdge.Floating, monitor);
            bar.PlaceFloating(null, null, monitor);
        }
    }

    private static void AssertQueueBrowserKeepsTheLatestSource(JiraIssue issue)
    {
        var initial = new TaskCompletionSource<IReadOnlyList<JiraIssue>>();
        var current = new TaskCompletionSource<IReadOnlyList<JiraIssue>>();
        var closing = new TaskCompletionSource<IReadOnlyList<JiraIssue>>();
        CancellationToken initialRequest = default;
        CancellationToken closingRequest = default;
        var client = new NullJiraClient
        {
            Queues = [new JiraQueue("SD", "Open", "status = Open"), new JiraQueue("SD", "Waiting", "status = Waiting")],
            BrowseIssues = (jql, token) =>
            {
                if (jql == "assignee = currentUser()") return Task.FromResult<IReadOnlyList<JiraIssue>>([issue]);
                if (jql == "status = Waiting") return current.Task;
                if (!initialRequest.CanBeCanceled)
                {
                    initialRequest = token;
                    return initial.Task;
                }
                closingRequest = token;
                return closing.Task;
            },
        };
        var window = new QueueBrowserWindow(client, "assignee = currentUser()", (_, _) => "Pinned");
        try
        {
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            var source = (ComboBox)window.FindName("SourceBox");
            var status = (TextBlock)window.FindName("ResultsStatus");
            var grid = (DataGrid)window.FindName("TicketGrid");
            Assert.Equal(issue.Key, Assert.IsType<QueueBrowserWindow.TicketRow>(Assert.Single(grid.Items.Cast<object>())).Issue.Key);

            source.SelectedIndex = 1;
            Assert.True(initialRequest.CanBeCanceled);
            Assert.Contains("Loading", status.Text);
            source.SelectedIndex = 2;
            Assert.True(initialRequest.IsCancellationRequested);

            current.SetResult([issue]);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal(issue.Key, Assert.IsType<QueueBrowserWindow.TicketRow>(Assert.Single(grid.Items.Cast<object>())).Issue.Key);

            initial.SetResult([issue with { Key = "SD-99" }]);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal(issue.Key, Assert.IsType<QueueBrowserWindow.TicketRow>(Assert.Single(grid.Items.Cast<object>())).Issue.Key);

            source.SelectedIndex = 1;
            Assert.True(closingRequest.CanBeCanceled);
        }
        finally { window.Close(); }
        Assert.True(closingRequest.IsCancellationRequested);
        closing.SetCanceled();
    }

    private static void AssertAddedTicketsJoinTheQueue(AppController controller, BarWindow bar, JiraIssue issue)
    {
        const string requestedKey = "OLD-2";
        var manualIssue = issue with { Key = "TEST-2", Priority = new JiraPriority("4", "Low", 4) };
        var queueOnly = issue with { Key = "TEST-1", Priority = new JiraPriority("1", "Highest", 1) };
        var client = new NullJiraClient
        {
            QueueIssues = [queueOnly, issue],
            AddedLookups = [new(requestedKey, manualIssue), new("TEST-999", null)],
        };
        var clientProperty = typeof(AppController).GetProperty(nameof(AppController.Client))!;
        var original = controller.Client;
        clientProperty.SetValue(controller, client);
        controller.Settings.AdditionalTicketKeysBySite[client.Connection.SiteUrl.AbsoluteUri] =
            [issue.Key.ToLowerInvariant(), requestedKey, "TEST-999"];
        try
        {
            var (issues, pinnedCount, warning) = controller.FetchIssuesAsync().GetAwaiter().GetResult();
            Assert.Equal([requestedKey, "TEST-999"], client.RequestedAddedKeys.Order());
            Assert.Equal([issue.Key, manualIssue.Key, queueOnly.Key], issues.Select(ticket => ticket.Key));
            Assert.Equal(2, pinnedCount);
            Assert.Contains("one added ticket", warning);
            controller.Bar.StatusMessage = warning;
            bar.UpdateLayout();
            var indicator = (FrameworkElement)bar.FindName("RefreshWarning");
            Assert.Equal(Visibility.Visible, indicator.Visibility);
            Assert.Equal(warning, indicator.ToolTip);

            var newOrder = new[] { requestedKey, issue.Key, "TEST-999" };
            var site = client.Connection.SiteUrl.AbsoluteUri;
            controller.Settings.AdditionalTicketKeysBySite[site] = newOrder;
            var store = new JsonFileStore<AppSettings>(AppPaths.File("pin-order-test.json"));
            store.Save(controller.Settings);
            var persisted = store.Load()!.AdditionalTicketKeysBySite[site];
            Assert.Equal(newOrder, persisted);
            controller.Settings.AdditionalTicketKeysBySite[site] = persisted;
            (issues, _, _) = controller.FetchIssuesAsync().GetAwaiter().GetResult();
            Assert.Equal([manualIssue.Key, issue.Key, queueOnly.Key], issues.Select(ticket => ticket.Key));

            client.QueueIssues = [queueOnly];
            client.AddedLookups = [new(requestedKey, manualIssue), new(issue.Key, issue), new("TEST-999", null)];
            (issues, pinnedCount, _) = controller.FetchIssuesAsync().GetAwaiter().GetResult();
            Assert.Equal([manualIssue.Key, issue.Key, queueOnly.Key], issues.Select(ticket => ticket.Key));
            Assert.Equal(2, pinnedCount);
            client.QueueIssues = [queueOnly, issue];

            client.AddedLookups = [new(requestedKey, null, CanRetry: true), new("TEST-999", null)];
            (issues, _, warning) = controller.FetchIssuesAsync().GetAwaiter().GetResult();
            Assert.Equal([manualIssue.Key, issue.Key, queueOnly.Key], issues.Select(ticket => ticket.Key));
            Assert.Contains("previously loaded", warning);

            client.AddedLookups = [new(requestedKey, null), new("TEST-999", null)];
            (issues, pinnedCount, _) = controller.FetchIssuesAsync().GetAwaiter().GetResult();
            Assert.Equal([issue.Key, queueOnly.Key], issues.Select(ticket => ticket.Key));
            Assert.Equal(1, pinnedCount);
            client.AddedLookups = [new(requestedKey, manualIssue), new("TEST-999", null)];
            _ = controller.FetchIssuesAsync().GetAwaiter().GetResult();

            var otherClient = new NullJiraClient
            {
                Connection = new(new Uri("https://other.atlassian.net/"), "a@b.c", "other-token"),
                QueueIssues = [issue],
            };
            clientProperty.SetValue(controller, otherClient);
            (issues, pinnedCount, _) = controller.FetchIssuesAsync().GetAwaiter().GetResult();
            Assert.Empty(otherClient.RequestedAddedKeys);
            Assert.Equal(issue.Key, Assert.Single(issues).Key);
            Assert.Equal(0, pinnedCount);
            controller.Settings.AdditionalTicketKeysBySite[otherClient.Connection.SiteUrl.AbsoluteUri] = [requestedKey];
            otherClient.AddedLookups = [new(requestedKey, null, CanRetry: true)];
            (issues, _, _) = controller.FetchIssuesAsync().GetAwaiter().GetResult();
            Assert.Equal(issue.Key, Assert.Single(issues).Key);
        }
        finally
        {
            controller.Bar.StatusMessage = null;
            clientProperty.SetValue(controller, original);
        }
    }

    private static void AssertFilteredRunningTicketsPreservePinOrder(AppController controller, JiraIssue template)
    {
        var firstPin = template with { Key = "PIN-1", Summary = "Visible pin", Priority = new JiraPriority("5", "Low", 5) };
        var secondPin = template with { Key = "PIN-2", Summary = "Hidden pin", Priority = new JiraPriority("1", "Highest", 1) };
        var runningQueue = template with { Key = "QUEUE-1", Summary = "Hidden queue ticket", Priority = new JiraPriority("2", "High", 2) };
        var visibleQueue = template with { Key = "QUEUE-2", Summary = "Visible queue ticket", Priority = new JiraPriority("4", "Normal", 4) };
        var bar = controller.Bar;
        var issuesField = typeof(BarViewModel).GetField("_lastIssues", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pinsField = typeof(BarViewModel).GetField("_pinnedCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousIssues = issuesField.GetValue(bar);
        var previousPinCount = pinsField.GetValue(bar);
        var previousFilter = bar.Filter;
        issuesField.SetValue(bar, new[] { firstPin, secondPin, runningQueue, visibleQueue });
        pinsField.SetValue(bar, 2);
        try
        {
            bar.ApplyFilter(new TicketFilter { Summary = "Visible" });
            controller.Tracker.Start(runningQueue.Key);
            Assert.Equal([firstPin.Key, runningQueue.Key, visibleQueue.Key], bar.Tickets.Select(ticket => ticket.Key));
            Assert.True(bar.Tickets[1].IsStale);
            controller.Tracker.Stop(controller.Tracker.Active!.StartedUtc);

            controller.Tracker.Start(secondPin.Key);
            Assert.Equal([firstPin.Key, secondPin.Key, visibleQueue.Key], bar.Tickets.Select(ticket => ticket.Key));
            Assert.True(bar.Tickets[1].IsStale);
        }
        finally
        {
            if (controller.Tracker.Active is { } active) controller.Tracker.Stop(active.StartedUtc);
            issuesField.SetValue(bar, previousIssues);
            pinsField.SetValue(bar, previousPinCount);
            bar.ApplyFilter(previousFilter);
        }
    }

    private static void AssertSyncedOvernightWorklogKeepsItsDaySegment(JiraIssue issue)
    {
        const string outsideQuery = "OTHER-42";
        var started = new DateTimeOffset(DateTime.Today.AddHours(-1));
        var pending = new List<PendingWorklog> { new(Guid.NewGuid(), outsideQuery, started, 7200, "", 0) };
        var client = new NullJiraClient { Worklogs = [] };
        var calendar = new WorklogCalendarWindow(issue, client, () => pending);
        try
        {
            calendar.Show();
            ((ComboBox)calendar.FindName("ViewModeBox")).SelectedIndex = (int)WorklogCalendarView.Day;
            Assert.Contains("1:00:00", ((TextBlock)calendar.FindName("RangeTotal")).Text);

            pending.Clear();
            client.Worklogs = [new("11", outsideQuery, started, 7200, "Tester", null)];
            Assert.True(calendar.RefreshAsync().GetAwaiter().GetResult());
            var grid = CalendarField<Grid>(calendar, "_timeGrid");
            var overlay = Assert.Single(grid.Children.OfType<Grid>());
            var block = Assert.Single(Assert.Single(overlay.Children.OfType<Grid>()).Children.OfType<Border>());
            Assert.Contains("Click to edit the full worklog", Assert.IsType<string>(block.ToolTip));
            Assert.Contains("1:00:00", ((TextBlock)calendar.FindName("RangeTotal")).Text);
            Assert.Same(System.Windows.Media.Brushes.Black,
                WorklogCalendarWindow.WorklogTextBrush(System.Windows.Media.Color.FromRgb(0, 170, 0)));
        }
        finally { calendar.Close(); }
    }

    private static void AssertWeeklyTotalsUseDiscoveredWorklogsAndConfiguredStart(JiraIssue selected)
    {
        const string outsideQueue = "OTHER-42";
        const string accountId = "test-account";
        var sunday = new DateOnly(2026, 11, 15);
        var outsideIssue = new JiraIssue(outsideQueue, "Unassigned ticket", null, "Done", "software", "Task");
        JiraWorklog Worklog(string id, string key, int day, int hour, int minute, int minutes, string author = accountId)
        {
            var started = WorklogCalendarLayout.LocalDateTime(sunday.AddDays(day), new TimeOnly(hour, minute), TimeZoneInfo.Local);
            return new JiraWorklog(id, key, started, minutes * 60, "Tester", null) { AuthorAccountId = author };
        }

        var client = new NullJiraClient
        {
            Discovered = [outsideIssue],
            Worklogs =
            [
                Worklog("1", outsideQueue, -1, 10, 0, 20),
                Worklog("2", outsideQueue, 0, 23, 30, 75),
                Worklog("3", outsideQueue, 1, 10, 0, 45),
                Worklog("4", selected.Key, 2, 10, 0, 35),
                Worklog("5", outsideQueue, 3, 14, 0, 90),
                Worklog("6", outsideQueue, 3, 16, 0, 60, "different-account"),
            ],
        };
        var settings = new AppSettings { CalendarFirstDayOfWeek = DayOfWeek.Sunday };
        var calendar = new WorklogCalendarWindow(selected, client, () => [], settings: () => settings);
        typeof(WorklogCalendarWindow).GetField("_anchor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(calendar, sunday.AddDays(3));
        try
        {
            calendar.Show();
            Assert.True(calendar.RefreshAsync().GetAwaiter().GetResult());
            Assert.Equal("Total: 4:05:00", ((TextBlock)calendar.FindName("RangeTotal")).Text);
            Assert.Equal(new WorklogCalendarRange(sunday, sunday.AddDays(7)),
                CalendarField<WorklogCalendarRange>(calendar, "_timeRange"));

            settings.CalendarFirstDayOfWeek = DayOfWeek.Monday;
            Assert.True(calendar.RefreshAsync().GetAwaiter().GetResult());
            Assert.Equal("Total: 3:35:00", ((TextBlock)calendar.FindName("RangeTotal")).Text);
            Assert.Equal(new WorklogCalendarRange(sunday.AddDays(1), sunday.AddDays(8)),
                CalendarField<WorklogCalendarRange>(calendar, "_timeRange"));

            settings.CalendarFirstDayOfWeek = DayOfWeek.Sunday;
            var overlapOnlyIssue = outsideIssue with { Key = "OTHER-43" };
            client.Discovered = [outsideIssue, overlapOnlyIssue];
            client.Worklogs = [.. client.Worklogs!, Worklog("7", overlapOnlyIssue.Key, -3, 12, 0, 72 * 60)];
            Assert.True(calendar.RefreshAsync().GetAwaiter().GetResult());
            Assert.Equal("Total: 16:05:00", ((TextBlock)calendar.FindName("RangeTotal")).Text);

            ((ComboBox)calendar.FindName("ViewModeBox")).SelectedIndex = (int)WorklogCalendarView.Month;
            Assert.Equal(new DateOnly(2026, 12, 3), client.DiscoveryUntil);
            var scheduler = (Grid)calendar.FindName("SchedulerHost");
            var month = Assert.IsType<Grid>(Assert.Single(scheduler.Children.Cast<UIElement>()));
            var december = Assert.Single(month.Children.OfType<Border>(), cell => Equals(cell.Tag, new DateOnly(2026, 12, 1)));
            december.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            Assert.Equal(new DateOnly(2027, 1, 3), client.DiscoveryUntil);
        }
        finally { calendar.Close(); }
    }

    private static void AssertCalendarFiltersPreserveFullTotals(JiraIssue issue)
    {
        var sunday = new DateOnly(2026, 11, 15);
        var tuesday = sunday.AddDays(2);
        var thursday = sunday.AddDays(4);
        PendingWorklog Worklog(DateOnly date, int hour, int minutes) => new(Guid.NewGuid(), issue.Key,
            WorklogCalendarLayout.LocalDateTime(date, new TimeOnly(hour, 0), TimeZoneInfo.Local), minutes * 60, "", 0);
        PendingWorklog[] logs = [Worklog(tuesday, 8, 45), Worklog(tuesday, 9, 30), Worklog(tuesday, 11, 15),
            Worklog(sunday.AddDays(3), 12, 60)];
        var settings = new AppSettings
        {
            CalendarFirstDayOfWeek = DayOfWeek.Sunday,
            CalendarWorkdays = [DayOfWeek.Tuesday, DayOfWeek.Thursday],
            CalendarWorkStartMinute = 8 * 60 + 15,
            CalendarWorkEndMinute = 10 * 60 + 45,
            ShowOnlyCalendarWorkdays = true,
            ShowOnlyCalendarWorkHours = true,
        };
        var calendar = new WorklogCalendarWindow(issue, new NullJiraClient(), () => logs, settings: () => settings);
        typeof(WorklogCalendarWindow).GetField("_anchor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(calendar, sunday.AddDays(3));
        try
        {
            calendar.Show();
            calendar.UpdateLayout();
            Assert.Equal(new[] { tuesday, thursday }, CalendarField<DateOnly[]>(calendar, "_timeDays"));
            Assert.Equal("Total: 2:30:00", ((TextBlock)calendar.FindName("RangeTotal")).Text);
            var grid = CalendarField<Grid>(calendar, "_timeGrid");
            Assert.Equal(3, grid.ColumnDefinitions.Count);
            Assert.Equal(7, grid.RowDefinitions.Count);
            Assert.Equal(18, grid.RowDefinitions[1].Height.Value);
            Assert.Equal(18, grid.RowDefinitions[^1].Height.Value);
            Assert.Equal(4, grid.Children.OfType<System.Windows.Shapes.Line>().Count());
            var overlay = Assert.Single(grid.Children.OfType<Grid>());
            Assert.Equal(2, overlay.Children.Count);
            var clipped = Assert.Single(overlay.Children.OfType<Grid>(), host => Grid.GetRow(host) == 0);
            Assert.Contains("08:15", Assert.IsType<string>(Assert.Single(clipped.Children.OfType<Border>()).ToolTip));
            Assert.Equal(2, Grid.GetRowSpan(clipped));

            var scheduler = (Grid)calendar.FindName("SchedulerHost");
            var view = (ComboBox)calendar.FindName("ViewModeBox");
            view.SelectedIndex = (int)WorklogCalendarView.Month;
            Assert.Equal(2, Assert.IsType<Grid>(Assert.Single(scheduler.Children.Cast<UIElement>())).ColumnDefinitions.Count);
            view.SelectedIndex = (int)WorklogCalendarView.Day;
            Assert.Equal(thursday, Assert.Single(CalendarField<DateOnly[]>(calendar, "_timeDays")));
        }
        finally { calendar.Close(); }
    }

    private static void AssertCalendarWorklogsStayInDayColumns(JiraIssue issue)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var regionalFirstDay = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var firstDay = (DayOfWeek)(((int)regionalFirstDay + 1) % 7);
        var start = today.AddDays(-(7 + (int)today.DayOfWeek - (int)firstDay) % 7);
        var pending = Enumerable.Range(0, 7).SelectMany(day => Enumerable.Range(0, day % 2 + 1)
            .Select(lane => new PendingWorklog(Guid.NewGuid(), issue.Key,
                new DateTimeOffset(start.AddDays(day).ToDateTime(new TimeOnly(9, lane * 10))).ToUniversalTime(),
                3600, new string('W', 200), 0))).ToList();
        var now = DateTimeOffset.UtcNow;
        var active = new ActiveSession(issue.Key, now.AddMinutes(-2), now);
        var settings = new AppSettings
        {
            CalendarFirstDayOfWeek = firstDay,
            DailyWorklogTargetMinutes = 60,
            WorklogTypeColors = [new("SD", issue.IssueType, "#203050")],
        };
        var calendar = new WorklogCalendarWindow(issue, new NullJiraClient(), () => pending,
            active: () => active, settings: () => settings);
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
                    var layout = Assert.IsType<DockPanel>(Assert.Single(host.Children.Cast<UIElement>()));
                    var headings = Assert.IsType<Grid>(layout.Children[0]);
                    var totals = Assert.IsType<Grid>(layout.Children[1]);
                    var scroll = Assert.IsType<ScrollViewer>(layout.Children[2]);
                    var grid = Assert.IsType<Grid>(scroll.Content);
                    var overlay = Assert.Single(grid.Children.OfType<Grid>());
                    var days = grid.ColumnDefinitions.Count - 1;
                    Assert.Equal(days, overlay.ColumnDefinitions.Count);
                    Assert.Equal(grid.ActualWidth, headings.ActualWidth, 1);
                    Assert.Equal(grid.ActualWidth, totals.ActualWidth, 1);
                    Assert.Equal(days, headings.Children.Count);
                    Assert.Equal(days + 1, totals.Children.Count);
                    Assert.Contains(overlay.Children.OfType<Grid>().SelectMany(entry => entry.Children.OfType<Border>()),
                        block => block.ToolTip is string text && text.Contains("Running timer", StringComparison.Ordinal));
                    if (mode == 1)
                    {
                        Assert.Equal(new WorklogCalendarRange(start, start.AddDays(7)),
                            CalendarField<WorklogCalendarRange>(calendar, "_timeRange"));
                        var oneLogDay = Enumerable.Range(0, 7).First(day => day % 2 == 0 && start.AddDays(day) != today);
                        var total = Assert.IsType<Border>(totals.Children[oneLogDay + 1]);
                        var run = Assert.IsType<System.Windows.Documents.Run>(Assert.IsType<TextBlock>(total.Child).Inlines.FirstInline);
                        Assert.Equal("1:00:00", run.Text);
                        Assert.Same(System.Windows.Media.Brushes.DarkGreen, run.Foreground);
                    }
                    Assert.True(grid.ActualWidth <= scroll.ViewportWidth + 0.1,
                        "Long worklog text must not expand the calendar beyond the viewport.");
                    Assert.NotEmpty(overlay.Children.Cast<UIElement>());
                    foreach (var entryHost in overlay.Children.OfType<Grid>())
                    {
                        var block = Assert.Single(entryHost.Children.OfType<Border>());
                        var day = Grid.GetColumn(entryHost);
                        var column = grid.ColumnDefinitions[day + 1];
                        var left = block.TranslatePoint(new Point(0, 0), grid).X;
                        Assert.True(left >= column.Offset - 0.1 && left + block.ActualWidth <= column.Offset + column.ActualWidth + 0.1,
                            $"Worklog bounds {left}–{left + block.ActualWidth} exceed day {day} bounds " +
                            $"{column.Offset}–{column.Offset + column.ActualWidth} at width {width} in mode {mode}.");
                        Assert.InRange(block.ActualWidth, column.ActualWidth / entryHost.ColumnDefinitions.Count - 5,
                            column.ActualWidth / entryHost.ColumnDefinitions.Count - 3);
                    }
                    var headingTop = headings.TranslatePoint(new Point(0, 0), host).Y;
                    scroll.ScrollToVerticalOffset(543);
                    calendar.UpdateLayout();
                    Assert.Equal(headingTop, headings.TranslatePoint(new Point(0, 0), host).Y);
                    var offset = scroll.VerticalOffset;
                    typeof(WorklogCalendarWindow).GetMethod("RenderScheduler", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(calendar, [false]);
                    calendar.UpdateLayout();
                    var replacement = CalendarField<ScrollViewer>(calendar, "_timeScroll");
                    Assert.Equal(offset, replacement.VerticalOffset, 1);
                }
            }
        }
        finally { calendar.Close(); }
    }

    private static void AssertCalendarClickEditsSavedWorklog(JiraIssue issue)
    {
        var started = new DateTimeOffset(DateTime.Today.AddHours(9));
        var original = new JiraWorklog("123", issue.Key, started, 60, "Tester", "Original description");
        var client = new NullJiraClient { Worklogs = [original], FailDiscovery = true };
        var calendar = new WorklogCalendarWindow(issue, client, () => [], resolutionMinutes: 5);
        try
        {
            calendar.Show();
            calendar.RefreshAsync().GetAwaiter().GetResult();
            var warning = (TextBlock)calendar.FindName("LoadWarningText");
            Assert.Equal(Visibility.Visible, warning.Visibility);
            calendar.UpdateLayout();
            var scroll = CalendarField<ScrollViewer>(calendar, "_timeScroll");
            scroll.ScrollToVerticalOffset(1500);
            calendar.UpdateLayout();
            var offset = scroll.VerticalOffset;
            var grid = CalendarField<Grid>(calendar, "_timeGrid");
            // Every resolution boundary has a label, including the five-minute divisions.
            Assert.Equal(288, grid.Children.OfType<TextBlock>().Count());
            var overlay = Assert.Single(grid.Children.OfType<Grid>());
            var host = Assert.Single(overlay.Children.OfType<Grid>());
            var block = Assert.Single(host.Children.OfType<Border>());
            Assert.True(block.ClipToBounds);
            var hit = grid.InputHitTest(block.TranslatePoint(new Point(block.ActualWidth / 2, 3), grid));
            Assert.NotNull(hit);
            var visual = hit as DependencyObject;
            while (visual is not null && visual != block)
                visual = System.Windows.Media.VisualTreeHelper.GetParent(visual);
            Assert.Same(block, visual);

            Exception? dialogError = null;
            calendar.Dispatcher.BeginInvoke(() =>
            {
                var editor = calendar.OwnedWindows.OfType<AddWorklogWindow>().Single();
                try
                {
                    Assert.Equal("Edit worklog", editor.Title);
                    Assert.False(((ComboBox)editor.FindName("TicketBox")).IsEnabled);
                    Assert.True(((ComboBox)editor.FindName("DescriptionTargetBox")).IsEnabled);
                    Assert.Equal("Original description", ((TextBox)editor.FindName("DescriptionBox")).Text);
                    ((TextBox)editor.FindName("DurationBox")).Text = "0:10";
                    ((ComboBox)editor.FindName("DescriptionTargetBox")).SelectedIndex = 1;
                    ((TextBox)editor.FindName("DescriptionBox")).Text = "Internal progress update";
                    typeof(AddWorklogWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(editor, [editor, new RoutedEventArgs()]);
                    Assert.True(editor.WorklogSaved);
                }
                catch (Exception ex) { dialogError = ex; }
                finally { editor.Close(); }
            }, DispatcherPriority.Background);
            block.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            block.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            if (dialogError is not null) ExceptionDispatchInfo.Capture(dialogError).Throw();
            Assert.NotNull(client.UpdatedWorklog);
            Assert.Equal("123", client.UpdatedWorklog.Id);
            Assert.Equal(600, client.UpdatedSeconds);
            Assert.Equal(original.Comment, client.Worklogs!.Single().Comment);
            Assert.Equal(original.CommentDocument, client.Worklogs!.Single().CommentDocument);
            Assert.Equal((issue.Key, "123", "Internal progress update"), client.AddedWorklogComment);
            calendar.UpdateLayout();
            Assert.Equal(offset, CalendarField<ScrollViewer>(calendar, "_timeScroll").VerticalOffset, 1);

            // Moves and duration changes use the same save path and retain the viewport too.
            foreach (var moved in new[] { true, false })
            {
                grid = CalendarField<Grid>(calendar, "_timeGrid");
                overlay = Assert.Single(grid.Children.OfType<Grid>());
                block = Assert.Single(Assert.Single(overlay.Children.OfType<Grid>()).Children.OfType<Border>());
                var current = client.Worklogs!.Single();
                var save = (Task)typeof(WorklogCalendarWindow).GetMethod("SaveCalendarEditAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(calendar,
                    [block.Tag, moved ? current.Started.AddHours(1) : current.Started,
                        moved ? current.TimeSpentSeconds : 1200])!;
                save.GetAwaiter().GetResult();
                calendar.UpdateLayout();
                Assert.Equal(offset, CalendarField<ScrollViewer>(calendar, "_timeScroll").VerticalOffset, 1);
                Assert.Equal(moved ? 600 : 1200, client.Worklogs!.Single().TimeSpentSeconds);
                Assert.Equal(Visibility.Visible, warning.Visibility);
            }
            client.FailDiscovery = false;
            Assert.True(calendar.RefreshAsync().GetAwaiter().GetResult());
            Assert.Equal(Visibility.Collapsed, warning.Visibility);

            // A concurrent server edit must leave the editor open and avoid another PUT.
            client.Worklogs = [original with { TimeSpentSeconds = 120 }];
            client.UpdatedWorklog = null;
            var conflict = new AddWorklogWindow([issue], issue, started, TimeSpan.FromMinutes(1), client, original);
            try
            {
                typeof(AddWorklogWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(conflict, [conflict, new RoutedEventArgs()]);
                Assert.False(conflict.WorklogSaved);
                Assert.Null(client.UpdatedWorklog);
                Assert.Contains("changed in Jira", ((Wpf.Ui.Controls.InfoBar)conflict.FindName("Status")).Message);
            }
            finally { conflict.Close(); }
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
        Assert.Equal(now.TimeOfDay.TotalMinutes / resolution * Math.Max(16, resolution * 1.2),
            line.Margin.Top, 6);
        Assert.Equal(2, line.Height);
        Assert.False(line.IsHitTestVisible);
        Assert.True(Panel.GetZIndex(line) > 2); // Above saved worklogs.

        SetTime(now.AddMinutes(1));
        Assert.True(line.Margin.Top > now.TimeOfDay.TotalMinutes / resolution * Math.Max(16, resolution * 1.2));
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

    [Fact]
    public void Context_date_picker_commits_backslash_dates()
    {
        RunOnSta(() =>
        {
            var picker = new ContextDatePicker
            {
                Language = System.Windows.Markup.XmlLanguage.GetLanguage("en-GB"),
                SelectedDateFormat = DatePickerFormat.Short,
                SelectedDate = new DateTime(2026, 11, 3)
            };
            using var source = new HwndSource(new HwndSourceParameters("Context date picker test")
            {
                Width = 200,
                Height = 60
            });
            var next = new System.Windows.Controls.Button();
            source.RootVisual = new StackPanel { Children = { picker, next } };
            picker.ApplyTemplate();
            var editor = (DatePickerTextBox)picker.Template.FindName("PART_TextBox", picker);
            Assert.Equal(@"03\11\2026", editor.Text);

            editor.Text = @"05\11\2026";
            var menu = new ContextMenu();
            Assert.True(FocusManager.GetIsFocusScope(menu));
            editor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, editor, menu)
            {
                RoutedEvent = Keyboard.PreviewLostKeyboardFocusEvent
            });
            editor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, menu, editor)
            {
                RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent
            });
            Assert.Equal(@"05\11\2026", editor.Text);
            Assert.Equal(new DateTime(2026, 11, 3), picker.SelectedDate);

            editor.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            editor.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            Assert.Equal(new DateTime(2026, 11, 5), picker.SelectedDate);
            Assert.Equal(@"05\11\2026", editor.Text);

            editor.Text = @"06\11\2026";
            editor.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, editor, next)
            {
                RoutedEvent = Keyboard.PreviewLostKeyboardFocusEvent
            });
            editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert.Equal(new DateTime(2026, 11, 6), picker.SelectedDate);
            Assert.Equal(@"06\11\2026", editor.Text);
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

    private sealed class SmokeTestApp : App
    {
        // Modal dialogs can pump the dispatcher during tests. The harness owns application startup.
        protected override void OnStartup(StartupEventArgs e) { }
    }

    private sealed class NullJiraClient : IJiraClient
    {
        public IReadOnlyList<JiraIssue> QueueIssues { get; set; } = [];
        public IReadOnlyList<JiraQueue> Queues { get; init; } = [];
        public Func<string, CancellationToken, Task<IReadOnlyList<JiraIssue>>>? BrowseIssues { get; init; }
        public Task<IReadOnlyList<JiraQueue>> GetQueuesAsync(CancellationToken ct = default) => Task.FromResult(Queues);
        public Task<IReadOnlyList<JiraIssue>> BrowseIssuesAsync(string jql, IReadOnlyList<JiraPriority> priorities,
            CancellationToken ct = default) => BrowseIssues?.Invoke(jql, ct) ?? SearchAsync(jql, priorities, ct);
        public IReadOnlyList<JiraIssueLookup> AddedLookups { get; set; } = [];
        public IReadOnlyList<string> RequestedAddedKeys { get; private set; } = [];
        public IReadOnlyList<JiraWorklog>? Worklogs { get; set; }
        public IReadOnlyList<JiraIssue> Discovered { get; set; } = [];
        public bool FailDiscovery { get; set; }
        public DateOnly? DiscoveryUntil { get; private set; }
        public JiraWorklog? UpdatedWorklog { get; set; }
        public int UpdatedSeconds { get; private set; }
        public (string IssueKey, string WorklogId, string Text)? AddedWorklogComment { get; private set; }
        public JiraConnection Connection { get; init; } = new(new Uri("https://example.atlassian.net/"), "a@b.c", "t");
        public Task<JiraUser> GetMyselfAsync(CancellationToken ct = default) => Task.FromResult(new JiraUser("test-account", "Tester", null));
        public Task<IReadOnlyList<JiraIssue>> FindWorklogIssuesAsync(string accountId, DateOnly until, CancellationToken ct = default)
        {
            DiscoveryUntil = until;
            return FailDiscovery ? throw new HttpRequestException("search unavailable") : Task.FromResult(Discovered);
        }
        public Task<JiraDurationSettings> GetTimeTrackingConfigurationAsync(CancellationToken ct = default) =>
            Task.FromResult(JiraDurationSettings.Default);
        public Task<IReadOnlyList<JiraPriority>> GetPrioritiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JiraPriority>>([]);
        public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default) => Task.FromResult(QueueIssues);
        public Task<IReadOnlyList<JiraIssueLookup>> FetchIssuesByKeyAsync(IReadOnlyList<string> keys, IReadOnlyList<JiraPriority> priorities, CancellationToken ct = default)
        {
            RequestedAddedKeys = keys;
            return Task.FromResult(AddedLookups);
        }
        public Task<JiraSla?> GetRequestSlaAsync(string issueKey, CancellationToken ct = default) => Task.FromResult<JiraSla?>(null);
        public Task<IReadOnlyList<JiraWorklog>> GetWorklogsAsync(string issueKey, CancellationToken ct = default) =>
            Worklogs is null ? throw new NotSupportedException()
                : Task.FromResult<IReadOnlyList<JiraWorklog>>(Worklogs.Where(worklog => worklog.IssueKey == issueKey)
                    .Select(worklog => worklog with { AuthorAccountId = worklog.AuthorAccountId ?? "test-account" }).ToList());
        public Task AddWorklogAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, string? comment = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateWorklogAsync(string issueKey, JiraWorklog worklog, DateTimeOffset startedLocal, int timeSpentSeconds, CancellationToken ct = default)
        {
            UpdatedWorklog = worklog;
            UpdatedSeconds = timeSpentSeconds;
            Worklogs = Worklogs?.Select(w => w.Id == worklog.Id ? worklog with
                { Started = startedLocal, TimeSpentSeconds = timeSpentSeconds } : w).ToList();
            return Task.CompletedTask;
        }
        public Task AddCommentAsync(string issueKey, string wikiMarkup, bool isInternal, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> AddWorklogWithIdAsync(string issueKey, DateTimeOffset startedLocal, int timeSpentSeconds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddWorklogCommentAsync(string issueKey, string worklogId, string wikiMarkup, CancellationToken ct = default)
        {
            AddedWorklogComment = (issueKey, worklogId, wikiMarkup);
            return Task.CompletedTask;
        }
        public Task<string> AddAttachmentAsync(string issueKey, string filePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]?> GetIssueTypeIconAsync(string avatarId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteWorklogAsync(string issueKey, string worklogId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
