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
            indicatorWindow.Close();
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

    private sealed class NullJiraClient : IJiraClient
    {
        public JiraConnection Connection { get; } = new(new Uri("https://example.atlassian.net/"), "a@b.c", "t");
        public Task<JiraUser> GetMyselfAsync(CancellationToken ct = default) => throw new NotSupportedException();
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
