using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
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
    private readonly Dictionary<string, string> _additionalTicketDrafts = new(StringComparer.OrdinalIgnoreCase);
    private string? _additionalTicketSite;
    private bool _ticketDraftsLoaded;
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
        SlaScopeRun.Text = JiraConnection.GranularSlaScope;
        // New sign-ins default to a scoped token; existing classic tokens stay classic until changed.
        (saved is { CloudId: null, IsDataCenter: false } ? ClassicRadio : ScopedRadio).IsChecked = true;
        AuthenticationBox.SelectedIndex = saved?.IsDataCenter == true ? 1 : 0;

        JqlBox.Text = current.Jql;
        foreach (var (site, keys) in current.AdditionalTicketKeysBySite ?? [])
            _additionalTicketDrafts[site] = string.Join(Environment.NewLine, keys ?? []);
        AdditionalTicketsBox.IsEnabled = false;
        _ticketDraftsLoaded = true;
        UpdateAdditionalTicketSite();
        RefreshBox.Value = current.RefreshMinutes;
        IdleBox.Value = current.IdlePromptMinutes;
        ShortSessionBox.SelectedIndex = current.ShortSessions == ShortSessionMode.RoundUpToMinute ? 0 : 1;
        CalendarResolutionBox.ItemsSource = WorklogCalendarLayout.SupportedResolutions
            .Select(minutes => $"{minutes} minutes").ToList();
        var resolutionIndex = Array.IndexOf(WorklogCalendarLayout.SupportedResolutions,
            WorklogCalendarLayout.NormalizeResolution(current.CalendarResolutionMinutes));
        CalendarResolutionBox.SelectedIndex = resolutionIndex;
        var dayNames = CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
        CalendarFirstDayBox.ItemsSource = new[] { "Use Windows regional settings" }.Concat(dayNames);
        CalendarFirstDayBox.SelectedIndex = current.CalendarFirstDayOfWeek is { } day && Enum.IsDefined(day)
            ? (int)day + 1 : 0;
        var workdays = WorklogCalendarLayout.Workdays(current.CalendarWorkdays);
        foreach (var weekday in Enum.GetValues<DayOfWeek>())
        {
            var name = dayNames[(int)weekday];
            WorkdaysPanel.Children.Add(new CheckBox
            {
                Content = name,
                Tag = weekday,
                IsChecked = workdays.Contains(weekday),
                ToolTip = $"Include {name} in your working week.",
                Margin = new Thickness(0, 0, 12, 4),
            });
        }
        var hours = WorklogCalendarHours.FromSettings(current.CalendarWorkStartMinute, current.CalendarWorkEndMinute);
        WorkStartBox.Text = FormatWorkTime(hours.StartMinute);
        WorkEndBox.Text = FormatWorkTime(hours.EndMinute);
        OnlyWorkdaysBox.IsChecked = current.ShowOnlyCalendarWorkdays;
        OnlyWorkHoursBox.IsChecked = current.ShowOnlyCalendarWorkHours;
        DailyTargetBox.Value = current.DailyWorklogTargetMinutes;
        WorklogColorsBox.Text = string.Join(Environment.NewLine,
            (current.WorklogTypeColors ?? []).Select(rule => $"{rule.Project} / {rule.IssueType} = {rule.Color}"));
        StartupSwitch.IsChecked = current.StartWithWindows;
        HideFromCaptureSwitch.IsChecked = current.HideFromScreenCapture;
        ShowTitlesSwitch.IsChecked = current.ShowTitlesOnBar;
        TokenHint.Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible;
        ForgetSignInButton.Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible;

        if (firstRun && !IsDataCenter)
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
    internal bool ForgetSignIn { get; private set; }

    internal bool IsDataCenter => AuthenticationBox.SelectedIndex == 1;
    private bool IsScoped => !IsDataCenter && ScopedRadio.IsChecked == true;
    private Uri ReadSite() => JiraConnection.NormalizeConnectionSite(SiteBox.Text, IsDataCenter);

    private void Authentication_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SecretLabel is null) return;
        TokenBox.Clear();
        IntroText.Text = IsDataCenter
            ? "Use this compatibility option when your Jira administrator allows username and password sign-in for the REST API."
            : "Connect to Jira Cloud with your Atlassian account email and an API token.";
        SiteBox.PlaceholderText = IsDataCenter ? "https://jira.company.com/jira" : "yourcompany.atlassian.net";
        SiteBox.ToolTip = IsDataCenter ? "Enter the HTTPS base URL, including the Jira context path if used." : "Enter your Jira Cloud site address.";
        LoginLabel.Content = IsDataCenter ? "_Username" : "_Email";
        EmailBox.PlaceholderText = IsDataCenter ? "Jira username" : "you@company.com";
        EmailBox.ToolTip = IsDataCenter ? "The Jira Data Center account username." : "The Atlassian account email that owns the API token.";
        SecretLabel.Content = IsDataCenter ? "_Password" : "API _token";
        TokenBox.ToolTip = IsDataCenter ? "Enter your Jira Data Center password." : "Paste a Jira Cloud API token.";
        TokenHint.Text = "Windows Credential Manager stores the sign-in. Leave the secret blank to reuse it for the saved connection.";
        CreateTokenLink.Visibility = TokenTypePanel.Visibility = IsDataCenter ? Visibility.Collapsed : Visibility.Visible;
        TokenType_Changed(sender, e);
        UpdateAdditionalTicketSite();
    }

    private void TokenType_Changed(object sender, RoutedEventArgs e)
    {
        // Raised during InitializeComponent before all controls exist.
        if (ScopedPanel is null || ClassicWarning is null || AuthenticationBox is null) return;
        ScopedPanel.Visibility = IsScoped ? Visibility.Visible : Visibility.Collapsed;
        ClassicWarning.Visibility = IsScoped || IsDataCenter ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SiteBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_ticketDraftsLoaded) return;
        var previous = _additionalTicketSite;
        UpdateAdditionalTicketSite();
        if (!string.Equals(previous, _additionalTicketSite, StringComparison.OrdinalIgnoreCase))
            CloudIdBox.Text = string.Equals(_additionalTicketSite, _saved?.SiteUrl, StringComparison.OrdinalIgnoreCase)
                ? _saved?.CloudId ?? string.Empty : string.Empty;
    }

    private void UpdateAdditionalTicketSite()
    {
        string? site;
        try { site = ReadSite().AbsoluteUri; }
        catch (ArgumentException) { site = null; }
        if (!_ticketDraftsLoaded || string.Equals(_additionalTicketSite, site, StringComparison.OrdinalIgnoreCase)) return;

        if (_additionalTicketSite is { } previous) _additionalTicketDrafts[previous] = AdditionalTicketsBox.Text;
        _additionalTicketSite = site;
        AdditionalTicketsBox.IsEnabled = site is not null;
        AdditionalTicketsBox.Text = site is not null && _additionalTicketDrafts.TryGetValue(site, out var text) ? text : string.Empty;
    }

    private void MoveTicketLineUp_Click(object sender, RoutedEventArgs e) => MoveTicketLine(-1);

    private void MoveTicketLineDown_Click(object sender, RoutedEventArgs e) => MoveTicketLine(1);

    private void MoveTicketLine(int direction)
    {
        var text = AdditionalTicketsBox.Text;
        var caret = Math.Clamp(AdditionalTicketsBox.CaretIndex, 0, text.Length);
        var line = NormalizeLineBreaks(text[..caret]).Count(character => character == '\n');
        var lines = NormalizeLineBreaks(text).Split('\n');
        if (line >= lines.Length || string.IsNullOrWhiteSpace(lines[line])) return;

        var destination = line + direction;
        while (destination >= 0 && destination < lines.Length && string.IsNullOrWhiteSpace(lines[destination]))
            destination += direction;
        if (destination < 0 || destination >= lines.Length) return;

        (lines[line], lines[destination]) = (lines[destination], lines[line]);
        AdditionalTicketsBox.Text = string.Join(Environment.NewLine, lines);
        var selection = lines.Take(destination).Sum(value => value.Length + Environment.NewLine.Length);
        AdditionalTicketsBox.Focus();
        AdditionalTicketsBox.Select(selection, lines[destination].Length);
    }

    private static string NormalizeLineBreaks(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// The token to use: a newly typed one, or the saved one if the site is unchanged.
    /// A saved token is never re-pointed at a different site without being re-entered.
    /// </summary>
    private string? ResolveToken(Uri site)
    {
        var entered = TokenBox.Password;
        if (entered.Length > 0) return IsDataCenter ? entered : entered.Trim();
        return _saved?.ReuseSecret(site, EmailBox.Text.Trim(), IsDataCenter);
    }

    private void ForgetSignIn_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(this, "Remove the saved Jira sign-in from Windows Credential Manager?",
            "Forget Jira sign-in", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;
        ForgetSignIn = true;
        DialogResult = true;
    }

    private void CreateToken_Click(object sender, RoutedEventArgs e) =>
        Shell.OpenUrl("https://id.atlassian.com/manage-profile/security/api-tokens");

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (await TestAsync() == TestOutcome.Invalid) SettingsTabs.SelectedItem = JiraTab;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var additionalKeys = AdditionalTicketsBox.Text.Split(['\r', '\n', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(key => key.ToUpperInvariant()).Where(seenKeys.Add).ToArray();
        if (additionalKeys.FirstOrDefault(key => !TicketKeyPattern().IsMatch(key)) is { } invalidKey)
        {
            SettingsTabs.SelectedItem = AddTicketsTab;
            ShowStatus(InfoBarSeverity.Error, $"Enter a Jira ticket key like ABC-123. Check the entry {invalidKey}.");
            return;
        }
        var workdays = WorkdaysPanel.Children.OfType<CheckBox>().Where(box => box.IsChecked == true)
            .Select(box => (DayOfWeek)box.Tag).ToArray();
        if (workdays.Length == 0)
        {
            SettingsTabs.SelectedItem = WorkWeekTab;
            ShowStatus(InfoBarSeverity.Error, "Choose at least one working day.");
            return;
        }
        if (!TryReadWorkTime(WorkStartBox.Text, out var start) || !TryReadWorkTime(WorkEndBox.Text, out var end) ||
            !WorklogCalendarHours.IsValid(start, end))
        {
            SettingsTabs.SelectedItem = WorkWeekTab;
            ShowStatus(InfoBarSeverity.Error, "Enter working hours as HH:mm with the end after the start. Use 24:00 for midnight at the end of the day.");
            return;
        }
        if (!TryReadWorklogColors(out var worklogColors))
        {
            SettingsTabs.SelectedItem = CalendarTab;
            return;
        }
        var outcome = await TestAsync();
        // Allow saving while offline, but not with credentials Jira has rejected.
        if (outcome == TestOutcome.Invalid)
        {
            SettingsTabs.SelectedItem = JiraTab;
            return;
        }
        if (IsScoped && _testedCloudId is null)
        {
            SettingsTabs.SelectedItem = JiraTab;
            ShowStatus(InfoBarSeverity.Error, "A scoped token needs the site's cloud ID – paste it from admin.atlassian.com.");
            return;
        }

        var site = ReadSite();
        Credentials = new StoredCredentials(site.ToString(), EmailBox.Text.Trim(), ResolveToken(site)!,
            IsScoped ? _testedCloudId : null, IsDataCenter);
        var siteKeys = new Dictionary<string, string[]>(Result.AdditionalTicketKeysBySite ?? [], StringComparer.OrdinalIgnoreCase);
        if (additionalKeys.Length == 0) siteKeys.Remove(site.AbsoluteUri);
        else siteKeys[site.AbsoluteUri] = additionalKeys;
        Result = new AppSettings
        {
            Jql = string.IsNullOrWhiteSpace(JqlBox.Text) ? AppSettings.DefaultJql : JqlBox.Text.Trim(),
            AdditionalTicketKeysBySite = siteKeys,
            RefreshMinutes = (int)Math.Clamp(RefreshBox.Value ?? 5, 1, 120),
            IdlePromptMinutes = (int)Math.Clamp(IdleBox.Value ?? 10, 0, 480),
            ShortSessions = ShortSessionBox.SelectedIndex == 0 ? ShortSessionMode.RoundUpToMinute : ShortSessionMode.Skip,
            CalendarResolutionMinutes = WorklogCalendarLayout.SupportedResolutions[
                Math.Max(0, CalendarResolutionBox.SelectedIndex)],
            CalendarFirstDayOfWeek = CalendarFirstDayBox.SelectedIndex > 0
                ? (DayOfWeek)(CalendarFirstDayBox.SelectedIndex - 1) : null,
            CalendarWorkdays = workdays,
            CalendarWorkStartMinute = start,
            CalendarWorkEndMinute = end,
            ShowOnlyCalendarWorkdays = OnlyWorkdaysBox.IsChecked == true,
            ShowOnlyCalendarWorkHours = OnlyWorkHoursBox.IsChecked == true,
            DailyWorklogTargetMinutes = DailyTargetBox.Value is > 0 and <= 1440 and var target ? (int)target : null,
            WorklogTypeColors = worklogColors,
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

    private static string FormatWorkTime(int minute) => FormattableString.Invariant($"{minute / 60:00}:{minute % 60:00}");

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]*-[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex TicketKeyPattern();

    private static bool TryReadWorkTime(string text, out int minute)
    {
        text = text.Trim();
        if (text == "24:00")
        {
            minute = 24 * 60;
            return true;
        }
        var valid = TimeOnly.TryParseExact(text, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time);
        minute = time.Hour * 60 + time.Minute;
        return valid;
    }

    private bool TryReadWorklogColors(out List<WorklogTypeColor> colors)
    {
        colors = [];
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in WorklogColorsBox.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var slash = line.IndexOf('/');
            var equals = line.LastIndexOf('=');
            if (slash > 0 && equals > slash + 1)
            {
                var project = line[..slash].Trim();
                var type = line[(slash + 1)..equals].Trim();
                var color = line[(equals + 1)..].Trim();
                if (project.Length > 0 && type.Length > 0 && color is ['#', _, _, _, _, _, _] &&
                    color.Skip(1).All(Uri.IsHexDigit) && keys.Add($"{project}/{type}"))
                {
                    colors.Add(new(project, type, color.ToUpperInvariant()));
                    continue;
                }
            }
            ShowStatus(InfoBarSeverity.Error, "Enter one unique worklog color per project and issue type, in the format IT / Incident = #3866A8.");
            return false;
        }
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private enum TestOutcome { Ok, Invalid, Unreachable }

    private async Task<TestOutcome> TestAsync()
    {
        _testedCloudId = null;
        Uri site;
        try
        {
            site = ReadSite();
        }
        catch (ArgumentException ex)
        {
            ShowStatus(InfoBarSeverity.Error, ex.Message);
            return TestOutcome.Invalid;
        }

        var email = EmailBox.Text.Trim();
        var token = ResolveToken(site);
        if (email.Length == 0 || string.IsNullOrEmpty(token) || (IsDataCenter && email.Contains(':')))
        {
            var siteChanged = _saved is not null && string.IsNullOrEmpty(TokenBox.Password);
            ShowStatus(InfoBarSeverity.Error, IsDataCenter ? "Enter the username and password for this Jira Data Center site. The username cannot contain a colon."
                : siteChanged
                ? "You changed the Jira site – enter the API token for that site."
                : "Email and API token are required.");
            return TestOutcome.Invalid;
        }

        SettingsTabs.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = false;
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
            var me = await new JiraClient(http, new JiraConnection(site, email, token, cloudId, IsDataCenter)).GetMyselfAsync();
            _testedCloudId = cloudId;
            if (IsDataCenter)
                ShowStatus(InfoBarSeverity.Success, "Connected to Jira Data Center.");
            else if (IsScoped)
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
            SettingsTabs.IsEnabled = TestButton.IsEnabled = SaveButton.IsEnabled = true;
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
