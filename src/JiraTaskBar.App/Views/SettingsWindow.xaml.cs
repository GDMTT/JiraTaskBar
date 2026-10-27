using System.Net.Http;
using System.Windows;
using JiraTaskBar.App.Services;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Storage;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace JiraTaskBar.App.Views;

public partial class SettingsWindow : FluentWindow
{
    private readonly bool _hasSavedToken;
    private readonly Func<string?> _savedToken;

    public SettingsWindow(AppSettings current, Func<string?> savedToken, bool firstRun)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _savedToken = savedToken;
        _hasSavedToken = !string.IsNullOrEmpty(savedToken());

        SiteBox.Text = current.SiteUrl;
        EmailBox.Text = current.Email;
        JqlBox.Text = current.Jql;
        RefreshBox.Value = current.RefreshMinutes;
        IdleBox.Value = current.IdlePromptMinutes;
        ShortSessionBox.SelectedIndex = current.ShortSessions == ShortSessionMode.RoundUpToMinute ? 0 : 1;
        StartupSwitch.IsChecked = current.StartWithWindows;
        TokenHint.Visibility = _hasSavedToken ? Visibility.Visible : Visibility.Collapsed;

        if (firstRun)
            IntroText.Text = "Welcome! Connect to Jira Cloud with your Atlassian account email and an API token " +
                             "(the same details Jira Assistant uses for token sign-in).";
        Result = current;
    }

    /// <summary>The edited settings (valid when the dialog returns true).</summary>
    public AppSettings Result { get; private set; }

    /// <summary>A newly entered token, or null to keep the saved one.</summary>
    public string? NewToken { get; private set; }

    private void CreateToken_Click(object sender, RoutedEventArgs e) =>
        Shell.OpenUrl("https://id.atlassian.com/manage-profile/security/api-tokens");

    private async void Test_Click(object sender, RoutedEventArgs e) => await TestAsync();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var outcome = await TestAsync();
        // Allow saving while offline, but not with credentials Jira has rejected.
        if (outcome == TestOutcome.Invalid) return;

        Result = new AppSettings
        {
            SiteUrl = JiraConnection.NormalizeSiteUrl(SiteBox.Text).ToString(),
            Email = EmailBox.Text.Trim(),
            Jql = string.IsNullOrWhiteSpace(JqlBox.Text) ? AppSettings.DefaultJql : JqlBox.Text.Trim(),
            RefreshMinutes = (int)Math.Clamp(RefreshBox.Value ?? 5, 1, 120),
            IdlePromptMinutes = (int)Math.Clamp(IdleBox.Value ?? 10, 0, 480),
            ShortSessions = ShortSessionBox.SelectedIndex == 0 ? ShortSessionMode.RoundUpToMinute : ShortSessionMode.Skip,
            StartWithWindows = StartupSwitch.IsChecked == true,
            Dock = Result.Dock,
            MonitorDevice = Result.MonitorDevice,
            FloatLeft = Result.FloatLeft,
            FloatTop = Result.FloatTop,
        };
        NewToken = string.IsNullOrEmpty(TokenBox.Password) ? null : TokenBox.Password.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private enum TestOutcome { Ok, Invalid, Unreachable }

    private async Task<TestOutcome> TestAsync()
    {
        Uri site;
        try
        {
            site = JiraConnection.NormalizeSiteUrl(SiteBox.Text);
        }
        catch (ArgumentException ex)
        {
            ShowStatus(InfoBarSeverity.Error, ex.Message);
            return TestOutcome.Invalid;
        }

        var email = EmailBox.Text.Trim();
        var token = string.IsNullOrEmpty(TokenBox.Password) ? _savedToken() : TokenBox.Password.Trim();
        if (email.Length == 0 || string.IsNullOrEmpty(token))
        {
            ShowStatus(InfoBarSeverity.Error, "Email and API token are required.");
            return TestOutcome.Invalid;
        }

        TestButton.IsEnabled = SaveButton.IsEnabled = false;
        ShowStatus(InfoBarSeverity.Informational, "Connecting…");
        try
        {
            using var http = JiraClient.CreateHttpClient();
            var me = await new JiraClient(http, new JiraConnection(site, email, token)).GetMyselfAsync();
            ShowStatus(InfoBarSeverity.Success, $"Connected as {me.DisplayName}.");
            return TestOutcome.Ok;
        }
        catch (JiraApiException ex)
        {
            ShowStatus(InfoBarSeverity.Error, ex.Message);
            return TestOutcome.Invalid;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ShowStatus(InfoBarSeverity.Warning, $"Couldn't reach {site.Host}: {ex.Message}");
            return TestOutcome.Unreachable;
        }
        finally
        {
            TestButton.IsEnabled = SaveButton.IsEnabled = true;
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }
}
