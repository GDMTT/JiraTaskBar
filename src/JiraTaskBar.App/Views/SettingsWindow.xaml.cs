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
    private readonly StoredCredentials? _saved;

    /// <param name="saved">The current credentials; the token is reused only while the site stays the same.</param>
    /// <param name="message">Optional reason shown at the top, e.g. "Your API token was rejected".</param>
    public SettingsWindow(AppSettings current, StoredCredentials? saved, bool firstRun, string? message = null)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _saved = saved;

        SiteBox.Text = saved?.SiteUrl ?? current.SiteUrl ?? string.Empty;
        EmailBox.Text = saved?.Email ?? current.Email ?? string.Empty;
        JqlBox.Text = current.Jql;
        RefreshBox.Value = current.RefreshMinutes;
        IdleBox.Value = current.IdlePromptMinutes;
        ShortSessionBox.SelectedIndex = current.ShortSessions == ShortSessionMode.RoundUpToMinute ? 0 : 1;
        StartupSwitch.IsChecked = current.StartWithWindows;
        TokenHint.Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible;

        if (firstRun)
            IntroText.Text = "Welcome! Connect to Jira Cloud with your Atlassian account email and an API token " +
                             "(the same details Jira Assistant uses for token sign-in).";
        if (message is not null)
            ShowStatus(InfoBarSeverity.Warning, message);
        Result = current;
    }

    /// <summary>The edited settings (valid when the dialog returns true).</summary>
    public AppSettings Result { get; private set; }

    /// <summary>The credentials to store (valid when the dialog returns true).</summary>
    public StoredCredentials? Credentials { get; private set; }

    /// <summary>
    /// The token to use: a newly typed one, or the saved one if the site is unchanged.
    /// A saved token is never re-pointed at a different site without being re-entered.
    /// </summary>
    private string? ResolveToken(Uri site)
    {
        if (!string.IsNullOrEmpty(TokenBox.Password)) return TokenBox.Password.Trim();
        return _saved is not null && string.Equals(_saved.SiteUrl, site.ToString(), StringComparison.OrdinalIgnoreCase)
            ? _saved.ApiToken
            : null;
    }

    private void CreateToken_Click(object sender, RoutedEventArgs e) =>
        Shell.OpenUrl("https://id.atlassian.com/manage-profile/security/api-tokens");

    private async void Test_Click(object sender, RoutedEventArgs e) => await TestAsync();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var outcome = await TestAsync();
        // Allow saving while offline, but not with credentials Jira has rejected.
        if (outcome == TestOutcome.Invalid) return;

        var site = JiraConnection.NormalizeSiteUrl(SiteBox.Text);
        Credentials = new StoredCredentials(site.ToString(), EmailBox.Text.Trim(), ResolveToken(site)!);
        Result = new AppSettings
        {
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
        var token = ResolveToken(site);
        if (email.Length == 0 || string.IsNullOrEmpty(token))
        {
            var siteChanged = _saved is not null && string.IsNullOrEmpty(TokenBox.Password);
            ShowStatus(InfoBarSeverity.Error, siteChanged
                ? "You changed the Jira site – enter the API token for that site."
                : "Email and API token are required.");
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
