using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using JiraTaskBar.App.Interop;
using JiraTaskBar.App.ViewModels;
using JiraTaskBar.App.Views;
using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Storage;
using JiraTaskBar.Core.Tracking;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace JiraTaskBar.App.Services;

/// <summary>Composition root: owns settings, the Jira client, the tracker, the bar window and the tray icon.</summary>
public sealed class AppController : IDisposable
{
    private readonly Application _app;
    private readonly JsonFileStore<AppSettings> _settingsStore = new(AppPaths.File("settings.json"));
    private readonly SecretStore _secrets = new(AppPaths.File("credentials.bin"), legacyTokenPath: AppPaths.File("token.bin"));
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _refresh = new();
    private readonly Dictionary<string, CommentWindow> _commentWindows = new(StringComparer.OrdinalIgnoreCase);
    private WorklogCalendarWindow? _worklogWindow;
    private readonly List<Task> _inflightWorklogs = [];

    private HttpClient? _http;
    private IReadOnlyList<JiraPriority>? _priorities;
    private JiraDurationSettings? _durationSettings;
    private IJiraClient? _additionalTicketClient;
    private Dictionary<string, JiraIssue> _additionalTicketCache = new(StringComparer.OrdinalIgnoreCase);
    private TaskbarIcon? _tray;
    private BarWindow? _bar;
    private ContextReportWindow? _contextReportWindow;
    private AboutWindow? _aboutWindow;
    private QueueBrowserWindow? _queueBrowser;
    private DateTimeOffset _lastHeartbeat;
    private DateTimeOffset? _awaySinceUtc;
    private bool _awayPromptOpen;
    private bool _contextHistoryVersionWarningLogged;
    private StoredCredentials? _credentials;

    public AppController(Application app)
    {
        _app = app;
        Settings = _settingsStore.Load() ?? new AppSettings();
        _credentials = LoadCredentials();
        Tracker = new TimeTracker(new JsonFileStore<TrackerState>(AppPaths.File("state.json")), TimeProvider.System);
        ContextHistory = new ContextSwitchHistory(
            new JsonFileStore<ContextSwitchHistoryState>(AppPaths.File("context-switch-history.json")));
        Worklogs = new WorklogService(() => Client,
            new WorklogQueue(new JsonFileStore<List<PendingWorklog>>(AppPaths.File("pending-worklogs.json"))),
            () => Settings.ShortSessions);
        Icons = new IssueTypeIcons(() => Client);
        Bar = new BarViewModel(this);

        Tracker.SessionCompleted += (_, session) =>
        {
            TrackWorklog(PostWorklogAsync(session));
            TrySaveContextHistory(() => ContextHistory.Record(session));
        };
        Tracker.Transitioned += (_, transition) =>
            TrySaveContextHistory(() =>
                ContextHistory.RecordSwitch(transition.PreviousKey, transition.CurrentKey, transition.OccurredUtc));
        Tracker.Changed += (_, _) =>
        {
            Bar.SyncActive();
            UpdateTrayTooltip();
            _worklogWindow?.RefreshLocalEntries();
        };
        Worklogs.QueueChanged += (_, _) => RefreshPendingWorklogState();
    }

    public AppSettings Settings { get; private set; }

    public IJiraClient? Client { get; private set; }

    public JiraDurationSettings DurationSettings => _durationSettings ?? JiraDurationSettings.Default;

    public TimeTracker Tracker { get; }

    public ContextSwitchHistory ContextHistory { get; }

    public WorklogService Worklogs { get; }

    public BarViewModel Bar { get; }

    public IssueTypeIcons Icons { get; }

    public bool IsExiting { get; private set; }

    public async Task StartAsync()
    {
        Log.Info("Starting Jira TaskBar");
        PurgeLeftovers();
        if (TrySite() is null)
        {
            if (!ShowSettingsDialog(firstRun: true))
            {
                _app.Shutdown();
                return;
            }
        }

        Connect();
        CreateTray();

        _bar = new BarWindow(Bar, this) { Opacity = 0 };
        _bar.Show();
        RestoreDock();
        ApplyDisplaySettings();
        _bar.Opacity = 1;

        await ResolveRecoveryAsync();
        Bar.PendingWorklogs = Worklogs.PendingCount;
        Bar.SyncActive();
        UpdateTrayTooltip();

        _tick.Tick += OnTick;
        _tick.Start();
        _refresh.Tick += async (_, _) =>
        {
            // Don't keep retrying a rejected token; the manual Refresh button still works.
            if (!IsAuthFailed) await Bar.RefreshAsync();
        };
        ApplyRefreshInterval();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        await Bar.RefreshAsync();
    }

    // ---- Jira ---------------------------------------------------------------------------

    private void Connect()
    {
        // Calendar windows retain the client they were opened with. Close them before replacing
        // and disposing that client so reopening always uses the current connection.
        _worklogWindow?.Close();
        _worklogWindow = null;
        _queueBrowser?.Close();
        _http?.Dispose();
        _http = null;
        Client = null;
        Bar.RefreshConnectionState();
        _priorities = null;
        _durationSettings = null;
        Icons.Reset();

        if (_credentials is null) return;

        _http = JiraClient.CreateHttpClient();
        try
        {
            // A cloud ID means a scoped token: calls go through api.atlassian.com instead of the site.
            Client = new JiraClient(_http, _credentials.CreateConnection());
        }
        catch (ArgumentException ex)
        {
            Log.Error("Stored credentials are invalid", ex);
        }
        finally
        {
            Bar.RefreshConnectionState();
        }
    }

    /// <summary>Analytics history is best-effort and must never interrupt timers or worklog delivery.</summary>
    private void TrySaveContextHistory(Action save)
    {
        if (!ContextHistory.IsWritable)
        {
            if (!_contextHistoryVersionWarningLogged)
            {
                Log.Error("Context-switch history was created by an unsupported version; history recording is disabled and the file was preserved");
                _contextHistoryVersionWarningLogged = true;
            }
            return;
        }

        try
        {
            save();
        }
        catch (Exception ex)
        {
            Log.Error("Could not save context-switch history; time tracking will continue", ex);
        }
    }

    /// <summary>
    /// Loads the encrypted site + email + token. A v1 install kept only the token encrypted (with the
    /// site in settings.json); it is migrated once into the combined blob and settings.json is cleaned.
    /// </summary>
    private StoredCredentials? LoadCredentials()
    {
        var credentials = _secrets.Load();
        var migrationComplete = credentials is not null;
        if (credentials is null && _secrets.LastLoadStatus == CredentialLoadStatus.Missing &&
            _secrets.LoadLegacyToken() is { Length: > 0 } legacyToken
            && !string.IsNullOrWhiteSpace(Settings.Email))
        {
            try
            {
                var site = JiraConnection.NormalizeSiteUrl(Settings.SiteUrl ?? string.Empty);
                credentials = new StoredCredentials(site.ToString(), Settings.Email.Trim(), legacyToken);
                try
                {
                    _secrets.Save(credentials);
                    migrationComplete = true;
                    Log.Info("Migrated credentials to Windows Credential Manager");
                }
                catch (CredentialBackendException ex)
                {
                    // Continue this session with the valid legacy credential. Its files and settings
                    // remain in place so a later startup can retry the vault migration.
                    Log.Error("Could not migrate credentials to Windows Credential Manager; legacy credentials were preserved", ex);
                }
            }
            catch (ArgumentException ex)
            {
                Log.Error($"Not migrating legacy token for site '{Settings.SiteUrl}': {ex.Message}");
            }
        }

        if (migrationComplete && (Settings.SiteUrl is not null || Settings.Email is not null))
        {
            Settings.SiteUrl = null;
            Settings.Email = null;
            SaveSettings();
        }
        return credentials;
    }

    /// <summary>The HTTPS Jira base URL from the saved credential.</summary>
    private Uri? TrySite()
    {
        if (_credentials is null) return null;
        try
        {
            return JiraConnection.NormalizeConnectionSite(_credentials.SiteUrl, _credentials.IsDataCenter);
        }
        catch (ArgumentException ex)
        {
            Log.Error("Ignoring an invalid stored Jira site", ex);
            return null;
        }
    }

    /// <summary>
    /// Removes pasted screenshots left behind by a crash and the preview's browser cache.
    /// Safe at startup: the single-instance mutex means no comment window is open.
    /// </summary>
    private static void PurgeLeftovers()
    {
        foreach (var name in new[] { "pending", "WebView2" })
        {
            var path = AppPaths.File(name);
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Could not clean up {path}", ex);
            }
        }
    }

    public async Task<(IReadOnlyList<JiraIssue> Issues, int PinnedCount, string? Warning)> FetchIssuesAsync()
    {
        var client = Client ?? throw new InvalidOperationException("Not connected to Jira.");
        var settings = Settings;
        if (!ReferenceEquals(client, _additionalTicketClient))
        {
            _additionalTicketClient = client;
            _additionalTicketCache = new(StringComparer.OrdinalIgnoreCase);
        }
        var additionalTicketCache = _additionalTicketCache;
        if (_priorities is null && client.Connection.IsDataCenter) await client.GetMyselfAsync();
        var priorities = _priorities ??= await client.GetPrioritiesAsync();
        try
        {
            _durationSettings ??= await client.GetTimeTrackingConfigurationAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or JiraApiException { StatusCode: 408 or 429 or >= 500 })
        {
            // Duration metadata is optional. Keep it uncached so a later refresh can retry.
            Log.Error("Could not load Jira duration settings; using default duration units", ex);
        }
        var issues = await client.SearchAsync(settings.Jql, priorities);
        var configuredKeys = (settings.AdditionalTicketKeysBySite?.GetValueOrDefault(client.Connection.SiteUrl.AbsoluteUri) ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key)).ToArray();
        var additionalKeys = configuredKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var obsoleteKey in additionalTicketCache.Keys.Where(key => !additionalKeys.Contains(key)).ToArray())
            additionalTicketCache.Remove(obsoleteKey);
        if (additionalKeys.Count == 0) return (issues, 0, null);

        foreach (var issue in issues)
            if (additionalKeys.Remove(issue.Key)) additionalTicketCache[issue.Key] = issue;

        IReadOnlyList<JiraIssueLookup> lookups = additionalKeys.Count == 0
            ? [] : await client.FetchIssuesByKeyAsync(additionalKeys.ToArray(), priorities);
        var failed = 0;
        var retained = 0;
        foreach (var lookup in lookups)
        {
            if (lookup.Issue is { } issue)
                additionalTicketCache[lookup.RequestedKey] = issue;
            else
            {
                failed++;
                if (lookup.CanRetry)
                {
                    if (additionalTicketCache.ContainsKey(lookup.RequestedKey)) retained++;
                }
                else additionalTicketCache.Remove(lookup.RequestedKey);
            }
        }
        var currentByKey = issues.Concat(lookups.Select(lookup => lookup.Issue).OfType<JiraIssue>())
            .GroupBy(issue => issue.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var pinned = configuredKeys.Select(key => additionalTicketCache.GetValueOrDefault(key)).OfType<JiraIssue>()
            .Select(issue => currentByKey.GetValueOrDefault(issue.Key, issue));
        var displayedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinctPins = pinned.Where(issue => displayedKeys.Add(issue.Key)).ToArray();
        var allIssues = distinctPins.Concat(issues.Where(issue => displayedKeys.Add(issue.Key))).ToArray();
        var warning = failed switch
        {
            0 => null,
            1 => "Could not refresh one added ticket.",
            _ => $"Could not refresh {failed} added tickets.",
        };
        if (retained > 0) warning += $" Showing previously loaded details for {retained} added ticket(s).";
        return (allIssues, distinctPins.Length, warning);
    }

    public void ToggleTimer(string key) => Tracker.Toggle(key);

    public void OpenInBrowser(string key)
    {
        if (TrySite() is not { } site) return;
        Shell.OpenUrl(new JiraConnection(site, string.Empty, string.Empty).BrowseUrl(key));
    }

    public void OpenCommentEditor(TicketViewModel ticket)
    {
        if (Client is null)
        {
            OpenSettings();
            return;
        }

        if (_commentWindows.TryGetValue(ticket.Key, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new CommentWindow(ticket.Issue, Client);
        _commentWindows[ticket.Key] = window;
        window.Closed += (_, _) => _commentWindows.Remove(ticket.Key);
        window.ShowNextTo(_bar is null ? IntPtr.Zero : new WindowInteropHelper(_bar).Handle);
    }

    public void OpenWorklogCalendar(TicketViewModel ticket)
    {
        if (Client is null) return;
        if (_worklogWindow is not null)
        {
            _worklogWindow.FocusIssue(ticket.Issue);
            _worklogWindow.Activate();
            return;
        }

        _worklogWindow = new WorklogCalendarWindow(ticket.Issue, Client, () => Worklogs.Pending,
            () => Bar.CalendarIssues, Settings.CalendarResolutionMinutes, () => Tracker.Active, () => Settings);
        _worklogWindow.Closed += (_, _) => _worklogWindow = null;
        _worklogWindow.Show();
    }

    private async Task PostWorklogAsync(CompletedSession session)
    {
        var result = await Worklogs.LogAsync(session);
        Log.Info($"Worklog {session.Key} {session.Duration} -> {result.Outcome} {result.Error}");
        Bar.PendingWorklogs = Worklogs.PendingCount;
        if (result.Outcome == WorklogOutcome.Posted)
            RefreshWorklogCalendars();
        if (result.IsAuthFailure)
        {
            ReportAuthFailure();
        }
        else if (result.Outcome == WorklogOutcome.Queued)
        {
            _tray?.ShowNotification("Worklog not saved yet",
                $"{session.Key}: {BarViewModel.FormatElapsed(session.Duration)} will be retried automatically.\n{result.Error}",
                NotificationIcon.Warning);
        }
    }

    private void RefreshWorklogCalendars()
    {
        if (!_app.Dispatcher.CheckAccess())
        {
            _ = _app.Dispatcher.InvokeAsync(RefreshWorklogCalendars);
            return;
        }

        if (_worklogWindow is { } window)
        {
            window.InvalidateWorklogs();
            _ = window.RefreshAsync();
        }
    }

    private void RefreshPendingWorklogState()
    {
        if (!_app.Dispatcher.CheckAccess())
        {
            _ = _app.Dispatcher.InvokeAsync(RefreshPendingWorklogState);
            return;
        }

        Bar.PendingWorklogs = Worklogs.PendingCount;
        _worklogWindow?.RefreshLocalEntries();
        RefreshWorklogCalendars();
    }

    private void TrackWorklog(Task task)
    {
        lock (_inflightWorklogs) _inflightWorklogs.Add(task);
        task.ContinueWith(t =>
        {
            lock (_inflightWorklogs) _inflightWorklogs.Remove(t);
        }, TaskScheduler.Default);
    }

    // ---- Timers, idle and recovery ------------------------------------------------------

    private void OnTick(object? sender, EventArgs e)
    {
        Bar.Tick();
        var now = DateTimeOffset.UtcNow;
        if (now - _lastHeartbeat >= TimeSpan.FromMinutes(1))
        {
            Tracker.Heartbeat();
            _lastHeartbeat = now;
        }
    }

    private void ApplyRefreshInterval()
    {
        _refresh.Interval = TimeSpan.FromMinutes(Math.Max(1, Settings.RefreshMinutes));
        _refresh.Stop();
        _refresh.Start();
    }

    private async Task ResolveRecoveryAsync()
    {
        if (Tracker.PendingRecovery is not { } pending) return;

        var start = pending.StartedUtc.ToLocalTime();
        var lastSeen = pending.LastSeenUtc.ToLocalTime();
        var box = new MessageBox
        {
            Title = "A timer was still running",
            Content = $"The timer for {pending.Key} was running when Jira TaskBar last closed.\n\n" +
                      $"Started:\t{start:ddd d MMM HH:mm}\n" +
                      $"Last seen:\t{lastSeen:ddd d MMM HH:mm}  ({BarViewModel.FormatElapsed(lastSeen - start)})\n\n" +
                      "What would you like to do?",
            PrimaryButtonText = "Log until last seen",
            SecondaryButtonText = "Keep running",
            CloseButtonText = "Discard",
        };
        var choice = await box.ShowDialogAsync() switch
        {
            MessageBoxResult.Primary => RecoveryChoice.LogUntilLastSeen,
            MessageBoxResult.Secondary => RecoveryChoice.KeepRunning,
            _ => RecoveryChoice.Discard,
        };

        if (choice == RecoveryChoice.Discard)
        {
            var confirm = new MessageBox
            {
                Title = "Discard time?",
                Content = $"Discard {BarViewModel.FormatElapsed(lastSeen - start)} on {pending.Key}? This can't be undone.",
                PrimaryButtonText = "Discard",
                CloseButtonText = "Log it instead",
            };
            if (await confirm.ShowDialogAsync() != MessageBoxResult.Primary)
                choice = RecoveryChoice.LogUntilLastSeen;
        }

        Tracker.ResolveRecovery(choice);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _app.Dispatcher.InvokeAsync(MarkAway);
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _app.Dispatcher.InvokeAsync(OnReturnedAsync);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _app.Dispatcher.InvokeAsync(MarkAway);
        else if (e.Mode == PowerModes.Resume) _app.Dispatcher.InvokeAsync(OnReturnedAsync);
    }

    private void MarkAway()
    {
        if (Tracker.Active is not null && _awaySinceUtc is null)
            _awaySinceUtc = DateTimeOffset.UtcNow;
    }

    private async Task OnReturnedAsync()
    {
        var since = _awaySinceUtc;
        _awaySinceUtc = null;
        if (since is null || Tracker.Active is not { } active || Settings.IdlePromptMinutes <= 0 || _awayPromptOpen) return;
        if (active.StartedUtc > since) return;

        var gap = DateTimeOffset.UtcNow - since.Value;
        if (gap < TimeSpan.FromMinutes(Settings.IdlePromptMinutes)) return;

        _awayPromptOpen = true;
        try
        {
            var box = new MessageBox
            {
                Title = "Welcome back",
                Content = $"You were away for {BarViewModel.FormatElapsed(gap)} (since {since.Value.ToLocalTime():HH:mm}) " +
                          $"while the timer for {active.Key} was running.\n\nKeep that time on {active.Key}?",
                PrimaryButtonText = "Remove time away",
                SecondaryButtonText = "Stop timer at " + since.Value.ToLocalTime().ToString("HH:mm"),
                CloseButtonText = "Keep it",
            };
            // Closing the dialog keeps the time, so nothing is lost by accident.
            switch (await box.ShowDialogAsync())
            {
                case MessageBoxResult.Primary:
                    if (Tracker.ActiveKey == active.Key) Tracker.ExcludeGap(since.Value);
                    break;
                case MessageBoxResult.Secondary:
                    if (Tracker.ActiveKey == active.Key) Tracker.Stop(since.Value);
                    break;
            }
        }
        finally
        {
            _awayPromptOpen = false;
        }
    }

    // ---- Docking ------------------------------------------------------------------------

    private void RestoreDock()
    {
        if (_bar is null) return;
        var monitor = MonitorInfo.ByDeviceOrPrimary(Settings.MonitorDevice);
        _bar.ApplyDock(Settings.Dock, monitor);
        if (Settings.Dock == DockEdge.Floating)
            _bar.PlaceFloating((int?)Settings.FloatLeft, (int?)Settings.FloatTop, monitor);
    }

    public void SetDock(DockEdge edge, MonitorInfo monitor)
    {
        if (_bar is null) return;
        _bar.ApplyDock(edge, monitor);
        if (edge == DockEdge.Floating)
        {
            var sameMonitor = string.Equals(Settings.MonitorDevice, monitor.Device, StringComparison.OrdinalIgnoreCase);
            _bar.PlaceFloating(sameMonitor ? (int?)Settings.FloatLeft : null, sameMonitor ? (int?)Settings.FloatTop : null, monitor);
            SaveFloatingPosition(monitor);
            return;
        }
        Settings.Dock = edge;
        Settings.MonitorDevice = monitor.Device;
        SaveSettings();
    }

    public void SaveFloatingPosition(MonitorInfo monitor)
    {
        if (_bar is null) return;
        var (x, y) = _bar.PhysicalPosition();
        Settings.Dock = DockEdge.Floating;
        Settings.MonitorDevice = monitor.Device;
        Settings.FloatLeft = x;
        Settings.FloatTop = y;
        SaveSettings();
    }

    // ---- Menus, tray, settings ----------------------------------------------------------

    public ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        PopulateMenu(menu);
        return menu;
    }

    private void PopulateMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(Item("Refresh tickets", SymbolRegular.ArrowSync24, async () => await Bar.RefreshAsync()));
        menu.Items.Add(Item("Browse queues…", null, OpenQueueBrowser));

        if (Tracker.Active is { } active)
            menu.Items.Add(Item($"Stop timer ({active.Key})", SymbolRegular.Stop24, () => Tracker.Stop()));

        menu.Items.Add(Item("Context switching…", null, OpenContextReport));

        var dock = Item("Dock", SymbolRegular.PanelLeft24, null);
        foreach (var edge in Enum.GetValues<DockEdge>())
        {
            var label = edge == DockEdge.Floating ? "Floating" : $"{edge} edge";
            var item = Item(label, null, () => SetDock(edge, CurrentMonitor()));
            item.IsCheckable = true;
            item.IsChecked = (_bar?.DockEdge ?? Settings.Dock) == edge;
            dock.Items.Add(item);
        }
        menu.Items.Add(dock);

        menu.Items.Add(Item(_bar?.IsVisible == true ? "Hide bar" : "Show bar", SymbolRegular.WindowNew24, ToggleBarVisibility));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", SymbolRegular.Settings24, OpenSettings));

        var startup = Item("Start with Windows", null, () =>
        {
            Settings.StartWithWindows = !Settings.StartWithWindows;
            ApplyStartup();
            SaveSettings();
        });
        startup.IsCheckable = true;
        startup.IsChecked = Settings.StartWithWindows;
        menu.Items.Add(startup);

        menu.Items.Add(Item("Open data folder", null, () => Shell.OpenFolder(AppPaths.DataDirectory)));
        menu.Items.Add(Item("About…", null, OpenAbout));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", SymbolRegular.Power24, () => _ = ExitAsync()));
    }

    private static MenuItem Item(string header, SymbolRegular? icon, Action? onClick)
    {
        var item = new MenuItem { Header = header };
        if (icon is { } symbol) item.Icon = new SymbolIcon { Symbol = symbol };
        if (onClick is not null) item.Click += (_, _) => onClick();
        return item;
    }

    private MonitorInfo CurrentMonitor() =>
        _bar is null ? MonitorInfo.Primary() : MonitorInfo.FromWindow(new WindowInteropHelper(_bar).Handle);

    private void CreateTray()
    {
        var accent = SystemParameters.WindowGlassColor;
        var trayMenu = new ContextMenu();
        trayMenu.Opened += (_, _) => PopulateMenu(trayMenu);
        PopulateMenu(trayMenu);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Jira TaskBar",
            IconSource = new GeneratedIconSource
            {
                Text = "J",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(accent.R, accent.G, accent.B)),
                FontWeight = FontWeights.Bold,
            },
            ContextMenu = trayMenu,
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => ToggleBarVisibility();
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;
        _tray.ToolTipText = Tracker.Active is { } active ? $"Jira TaskBar – timing {active.Key}" : "Jira TaskBar – no timer running";
    }

    private void ToggleBarVisibility()
    {
        if (_bar is null) return;
        if (_bar.IsVisible)
        {
            _bar.HideBar();
        }
        else
        {
            _bar.Show();
            RestoreDock();
        }
    }

    private void OpenSettings() => OpenSettings(message: null);

    private void OpenQueueBrowser()
    {
        if (Client is not { } client)
        {
            OpenSettings();
            return;
        }
        if (_queueBrowser is not null)
        {
            _queueBrowser.Activate();
            return;
        }
        _queueBrowser = new QueueBrowserWindow(client, Settings.Jql, (issue, direction) => EditBrowserPin(client, issue, direction));
        _queueBrowser.Closed += (_, _) => _queueBrowser = null;
        _queueBrowser.Show();
    }

    internal bool CanDropBrowserTicket(string key) => _queueBrowser?.DraggedTicket is { } issue &&
        string.Equals(issue.Key, key, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(Client, _queueBrowser.Client);

    internal void DropBrowserTicket(string key, string? anchor, bool after)
    {
        if (!CanDropBrowserTicket(key) || _queueBrowser is not { DraggedTicket: { } issue } browser) return;
        browser.ReportPinStatus(EditBrowserPin(browser.Client, issue, direction: 0, moveExisting: true, anchor, after));
    }

    private string EditBrowserPin(IJiraClient source, JiraIssue issue, int direction, bool moveExisting = false,
        string? anchor = null, bool after = false)
    {
        if (!ReferenceEquals(source, Client)) return "The Jira connection changed. Open Browse queues again.";
        var site = source.Connection.SiteUrl.AbsoluteUri;
        var saved = Settings.AdditionalTicketKeysBySite ??= new(StringComparer.Ordinal);
        var original = saved.GetValueOrDefault(site);
        var useCache = ReferenceEquals(source, _additionalTicketClient);
        var pins = (original ?? []).Select(key => useCache ? _additionalTicketCache.GetValueOrDefault(key)?.Key ?? key : key)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wasPinned = pins.Contains(issue.Key, StringComparer.OrdinalIgnoreCase);
        var changed = direction == 0 ? TicketPinOrder.Place(pins, issue.Key, anchor, after, moveExisting)
            : TicketPinOrder.Move(pins, issue.Key, direction);
        if (!changed) return direction == 0 ? $"{issue.Key} is already pinned."
            : $"{issue.Key} has no pinned ticket in that direction.";

        saved[site] = pins.ToArray();
        if (!SaveSettings())
        {
            if (original is null) saved.Remove(site);
            else saved[site] = original;
            return "Could not save the pinned ticket order.";
        }
        Bar.RequestRefresh();
        return wasPinned ? $"Moved {issue.Key} in the pinned list." : $"Pinned {issue.Key}.";
    }

    private void OpenAbout()
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow();
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
    }

    private void OpenContextReport()
    {
        if (_contextReportWindow is not null)
        {
            _contextReportWindow.Activate();
            return;
        }

        _contextReportWindow = new ContextReportWindow(ContextHistory);
        _contextReportWindow.Closed += (_, _) => _contextReportWindow = null;
        _contextReportWindow.Show();
    }

    /// <summary>Applies capture privacy and the ticket layout.</summary>
    private void ApplyDisplaySettings()
    {
        if (_bar is not null) CapturePrivacy.Apply(_bar, Settings.HideFromScreenCapture);
        _bar?.RefreshDockThickness();
        Bar.RefreshDisplay();
    }

    private void OpenSettings(string? message)
    {
        if (!ShowSettingsDialog(firstRun: false, message)) return;
        ApplyDisplaySettings();
        Connect();
        ApplyRefreshInterval();
        _ = Bar.RefreshAsync();
    }

    private bool ShowSettingsDialog(bool firstRun, string? message = null)
    {
        var window = new SettingsWindow(Settings, _credentials, firstRun, message);
        if (window.ShowDialog() != true) return false;
        if (window.ForgetSignIn)
        {
            try { _secrets.Clear(); }
            catch (Exception ex) when (ex is CredentialBackendException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not remove the saved Jira sign-in", ex);
                System.Windows.MessageBox.Show("Windows could not remove the saved Jira sign-in.", "Jira sign-in");
                return false;
            }
            _credentials = null;
            IsAuthFailed = false;
            return true;
        }
        if (window.Credentials is not { } credentials) return false;

        try { _secrets.Save(credentials); }
        catch (CredentialBackendException ex)
        {
            Log.Error("Could not save the Jira sign-in", ex);
            System.Windows.MessageBox.Show("Windows could not save the Jira sign-in.", "Jira sign-in");
            return false;
        }
        Settings = window.Result;
        _worklogWindow?.RefreshLocalEntries();
        _credentials = credentials;
        IsAuthFailed = false;
        SaveSettings();
        ApplyStartup();
        return true;
    }

    // ---- Expired / revoked token --------------------------------------------------------

    /// <summary>Set after Jira returns 401; automatic refreshes and retries pause until the token is updated.</summary>
    public bool IsAuthFailed { get; private set; }

    /// <summary>
    /// Called when Jira rejects the token. Prompts once (not on every refresh) and stops automatic
    /// calls, because repeated failed sign-ins can trigger Atlassian's account protection.
    /// </summary>
    public void ReportAuthFailure()
    {
        if (IsAuthFailed) return;
        IsAuthFailed = true;
        Log.Error("Jira rejected the saved sign-in (401); pausing automatic refresh");
        _ = PromptReauthenticateAsync();
    }

    public void ReportAuthSuccess() => IsAuthFailed = false;

    private bool _reauthPromptOpen;

    private async Task PromptReauthenticateAsync()
    {
        if (_reauthPromptOpen || IsExiting) return;
        _reauthPromptOpen = true;
        try
        {
            var dataCenter = _credentials?.IsDataCenter == true;
            var reason = dataCenter ? "Jira rejected the saved sign-in. Check the password and whether REST Basic sign-in is enabled."
                : "Jira rejected your API token. It may have expired or been revoked.";
            var box = new MessageBox
            {
                Title = "Jira sign-in needed",
                Content = reason + "\n\nTimers keep running and worklogs are saved until you sign in again.",
                PrimaryButtonText = "Update sign-in…",
                CloseButtonText = "Later",
            };
            if (await box.ShowDialogAsync() == MessageBoxResult.Primary)
                OpenSettings(reason);
        }
        finally
        {
            _reauthPromptOpen = false;
        }
    }

    private void ApplyStartup()
    {
        try
        {
            StartupRegistration.Apply(Settings.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log.Error("Could not update start-with-Windows setting", ex);
        }
    }

    private bool SaveSettings()
    {
        try
        {
            _settingsStore.Save(Settings);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
            return false;
        }
    }

    public async Task ShowPendingWorklogsAsync()
    {
        var pending = Worklogs.Pending;
        if (pending.Count == 0) return;

        var text = new StringBuilder("These worklogs couldn't be saved to Jira yet:\n\n");
        foreach (var p in pending)
            text.AppendLine($"• {p.Key}  {BarViewModel.FormatElapsed(TimeSpan.FromSeconds(p.Seconds))}  " +
                            $"from {p.StartedUtc.ToLocalTime():ddd HH:mm} – {p.LastError}");

        var box = new MessageBox
        {
            Title = "Pending worklogs",
            Content = text.ToString(),
            PrimaryButtonText = "Retry now",
            SecondaryButtonText = "Discard all",
            CloseButtonText = "Close",
        };
        switch (await box.ShowDialogAsync())
        {
            case MessageBoxResult.Primary:
                var client = Client;
                try { await Worklogs.RetryPendingAsync(); }
                catch (JiraApiException ex) when (ex.StatusCode == 401 && client is not null)
                {
                    Bar.ReportJiraSignInFailure(client, ex);
                }
                break;
            case MessageBoxResult.Secondary:
                foreach (var p in pending) Worklogs.Discard(p.Id);
                break;
        }
        Bar.PendingWorklogs = Worklogs.PendingCount;
    }

    // ---- Exit ---------------------------------------------------------------------------

    public async Task ExitAsync()
    {
        if (Tracker.Active is { } active)
        {
            var box = new MessageBox
            {
                Title = "Timer running",
                Content = $"The timer for {active.Key} is running ({BarViewModel.FormatElapsed(Tracker.Elapsed)}).",
                PrimaryButtonText = "Stop and log",
                SecondaryButtonText = "Exit, keep timer",
                CloseButtonText = "Cancel",
            };
            switch (await box.ShowDialogAsync())
            {
                case MessageBoxResult.Primary:
                    Tracker.Stop();
                    break;
                case MessageBoxResult.Secondary:
                    break; // state stays on disk; next start offers to keep it running
                default:
                    return;
            }
        }

        IsExiting = true;
        _tick.Stop();
        _refresh.Stop();

        Task[] inflight;
        lock (_inflightWorklogs) inflight = _inflightWorklogs.ToArray();
        await Task.WhenAny(Task.WhenAll(inflight), Task.Delay(TimeSpan.FromSeconds(10)));

        foreach (var w in _commentWindows.Values.ToList()) w.ForceClose();
        _worklogWindow?.Close();
        _contextReportWindow?.Close();
        _aboutWindow?.Close();
        _bar?.Close();
        _app.Shutdown();
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tray?.Dispose();
        _http?.Dispose();
    }
}
