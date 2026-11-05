using System.Net.Http;
using System.Windows;
using JiraTaskBar.App.Services;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Storage;
using JiraTaskBar.Core.Tracking;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace JiraTaskBar.App.Views;

public partial class SettingsWindow : FluentWindow
{
    private readonly StoredCredentials? _saved;
    /// <summary>The cloud ID confirmed by the last successful test (scoped tokens only).</summary>
    private string? _testedCloudId;

    /// <param name="saved">The current credentials; the token is reused only while the site stays the same.</param>
    /// <param name="message">Optional reason shown at the top, e.g. "Your API token was rejected".</param>
    public SettingsWindow(AppSettings current, StoredCredentials? saved, bool firstRun, string? message = null)
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        _saved = saved;

        SiteBox.Text = saved?.SiteUrl ?? current.SiteUrl ?? string.Empty;
        EmailBox.Text = saved?.Email ?? current.Email ?? string.Empty;
        CloudIdBox.Text = saved?.CloudId ?? string.Empty;
        ScopesRun.Text = JiraConnection.RequiredScopes;
        // New sign-ins default to a scoped token; existing classic tokens stay classic until changed.
        (saved is { CloudId: null } ? ClassicRadio : ScopedRadio).IsChecked = true;

        JqlBox.Text = current.Jql;
        RefreshBox.Value = current.RefreshMinutes;
        IdleBox.Value = current.IdlePromptMinutes;
        ShortSessionBox.SelectedIndex = current.ShortSessions == ShortSessionMode.RoundUpToMinute ? 0 : 1;
        CalendarResolutionBox.ItemsSource = WorklogCalendarLayout.SupportedResolutions
            .Select(minutes => $"{minutes} minutes").ToList();
        var resolutionIndex = Array.IndexOf(WorklogCalendarLayout.SupportedResolutions,
            WorklogCalendarLayout.NormalizeResolution(current.CalendarResolutionMinutes));
        CalendarResolutionBox.SelectedIndex = resolutionIndex;
        StartupSwitch.IsChecked = current.StartWithWindows;
        HideFromCaptureSwitch.IsChecked = current.HideFromScreenCapture;
        ShowTitlesSwitch.IsChecked = current.ShowTitlesOnBar;
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

    private bool IsScoped => ScopedRadio.IsChecked == true;

    private void TokenType_Changed(object sender, RoutedEventArgs e)
    {
        // Raised during InitializeComponent before all controls exist.
        if (ScopedPanel is null || ClassicWarning is null) return;
        ScopedPanel.Visibility = IsScoped ? Visibility.Visible : Visibility.Collapsed;
        ClassicWarning.Visibility = IsScoped ? Visibility.Collapsed : Visibility.Visible;
    }

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
        if (IsScoped && _testedCloudId is null)
        {
            ShowStatus(InfoBarSeverity.Error, "A scoped token needs the site's cloud ID – paste it from admin.atlassian.com.");
            return;
        }

        var site = JiraConnection.NormalizeSiteUrl(SiteBox.Text);
        Credentials = new StoredCredentials(site.ToString(), EmailBox.Text.Trim(), ResolveToken(site)!,
            IsScoped ? _testedCloudId : null);
        Result = new AppSettings
        {
            Jql = string.IsNullOrWhiteSpace(JqlBox.Text) ? AppSettings.DefaultJql : JqlBox.Text.Trim(),
            RefreshMinutes = (int)Math.Clamp(RefreshBox.Value ?? 5, 1, 120),
            IdlePromptMinutes = (int)Math.Clamp(IdleBox.Value ?? 10, 0, 480),
            ShortSessions = ShortSessionBox.SelectedIndex == 0 ? ShortSessionMode.RoundUpToMinute : ShortSessionMode.Skip,
            CalendarResolutionMinutes = WorklogCalendarLayout.SupportedResolutions[
                Math.Max(0, CalendarResolutionBox.SelectedIndex)],
            StartWithWindows = StartupSwitch.IsChecked == true,
            HideFromScreenCapture = HideFromCaptureSwitch.IsChecked == true,
            ShowTitlesOnBar = ShowTitlesSwitch.IsChecked == true,
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
        _testedCloudId = null;
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
            string? cloudId = null;
            if (IsScoped)
            {
                cloudId = await ResolveCloudIdAsync(site);
                if (cloudId is null) return TestOutcome.Invalid;
            }

            using var http = JiraClient.CreateHttpClient();
            var me = await new JiraClient(http, new JiraConnection(site, email, token, cloudId)).GetMyselfAsync();
            _testedCloudId = cloudId;
            if (IsScoped)
                ShowStatus(InfoBarSeverity.Success, $"Connected as {me.DisplayName} with a scoped token.");
            else
                ShowStatus(InfoBarSeverity.Warning, $"Connected as {me.DisplayName}. This classic token has full access to your Atlassian account – a scoped token is safer.");
            return TestOutcome.Ok;
        }
        catch (JiraApiException ex) when (ex.StatusCode is 401 or 403 && IsScoped)
        {
            ShowStatus(InfoBarSeverity.Error,
                $"Jira rejected the scoped token ({ex.StatusCode}). Check it was created with scopes for Jira – " +
                $"{JiraConnection.RequiredScopes} – and hasn't expired. If it's a classic token, choose \"Classic token\".");
            return TestOutcome.Invalid;
        }
        catch (JiraApiException ex)
        {
            ShowStatus(InfoBarSeverity.Error, ex.Message);
            return TestOutcome.Invalid;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ShowStatus(InfoBarSeverity.Warning, $"Couldn't reach Jira: {ex.Message}");
            // Offline: a scoped token can still be saved if the cloud ID was entered by hand.
            if (IsScoped && !string.IsNullOrWhiteSpace(CloudIdBox.Text))
                _testedCloudId = TryNormalizeCloudId(CloudIdBox.Text);
            return TestOutcome.Unreachable;
        }
        finally
        {
            TestButton.IsEnabled = SaveButton.IsEnabled = true;
        }
    }

    /// <summary>Uses the pasted cloud ID, or looks it up from the site (without credentials).</summary>
    private async Task<string?> ResolveCloudIdAsync(Uri site)
    {
        if (!string.IsNullOrWhiteSpace(CloudIdBox.Text))
        {
            var typed = TryNormalizeCloudId(CloudIdBox.Text);
            if (typed is null)
                ShowStatus(InfoBarSeverity.Error, "The cloud ID should look like 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d.");
            return typed;
        }

        using var anonymous = JiraClient.CreateHttpClient();
        var found = await JiraClient.TryGetCloudIdAsync(anonymous, site);
        if (found is null)
        {
            ShowStatus(InfoBarSeverity.Error,
                "Couldn't find the site's cloud ID automatically. Paste it into Cloud ID – on admin.atlassian.com " +
                "it's the value after /s/ in the address bar when you open the site.");
            return null;
        }
        CloudIdBox.Text = found;
        return found;
    }

    private static string? TryNormalizeCloudId(string text)
    {
        try { return JiraConnection.NormalizeCloudId(text); }
        catch (ArgumentException) { return null; }
    }

    private void ShowStatus(InfoBarSeverity severity, string message)
    {
        Status.Severity = severity;
        Status.Message = message;
        Status.IsOpen = true;
    }
}
