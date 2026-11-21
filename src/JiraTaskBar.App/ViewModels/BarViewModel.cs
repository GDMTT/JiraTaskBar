using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JiraTaskBar.App.Services;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.ViewModels;

public sealed partial class BarViewModel : ObservableObject
{
    private readonly AppController _app;
    private readonly Dictionary<string, TicketViewModel> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<JiraIssue> _lastIssues = [];
    private int _pinnedCount;
    private IJiraClient? _slaOwner;
    private long _lastFilterMinute = long.MinValue;
    private bool _refreshRequested;

    public BarViewModel(AppController app)
    {
        _app = app;
        Tickets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<TicketViewModel> Tickets { get; } = [];
    public TicketFilter Filter { get; private set; } = new();
    public IReadOnlyList<JiraIssue> CalendarIssues => _lastIssues.Concat(Tickets.Select(t => t.Issue))
        .DistinctBy(i => i.Key, StringComparer.OrdinalIgnoreCase).ToList();
    public string FilterLabel => Filter.IsActive ? "Filters ●" : "Filter…";
    public string FilterTooltip => Filter.IsActive
        ? "Taskbar filters active. The running ticket remains visible. Click to edit or reset."
        : "Filter by ticket type, time until due date, ticket number, and summary";
    public IReadOnlyList<string> AvailableTypes => _lastIssues.Select(i => i.IssueType)
        .Where(t => !string.IsNullOrWhiteSpace(t)).Concat(Filter.Types).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    public void ApplyFilter(TicketFilter filter)
    {
        Filter = filter;
        _lastFilterMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        OnPropertyChanged(nameof(FilterLabel));
        OnPropertyChanged(nameof(FilterTooltip));
        OnPropertyChanged(nameof(EmptyText));
        Rebuild();
        Tick();
    }

    public bool IsEmpty => Tickets.Count == 0;

    [ObservableProperty]
    private Orientation _orientation = Orientation.Vertical;

    /// <summary>Fixed button width for horizontal bars; NaN (auto/stretch) for vertical ones.</summary>
    [ObservableProperty]
    private double _buttonWidth = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(EmptyText))]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(PendingTooltip))]
    private int _pendingWorklogs;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public bool HasPending => PendingWorklogs > 0;

    public string PendingTooltip => $"{PendingWorklogs} worklog(s) not yet saved to Jira. Click for details.";

    public string EmptyText => IsRefreshing ? "Loading…" : HasStatus ? "⚠ " + StatusMessage :
        Filter.IsActive ? "No tickets match your filters" : "No tickets match your query";

    internal void RequestRefresh()
    {
        if (IsRefreshing) _refreshRequested = true;
        else _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        if (_app.Client is not { } client)
        {
            StatusMessage = "Not connected – open Settings";
            return;
        }

        IsRefreshing = true;
        try
        {
            var (issues, pinnedCount, warning) = await _app.FetchIssuesAsync();
            if (!ReferenceEquals(_app.Client, client)) return;
            if (ReferenceEquals(_slaOwner, client))
            {
                var previous = _lastIssues.Where(issue => issue.Sla is not null)
                    .DistinctBy(issue => issue.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(issue => issue.Key, issue => issue.Sla, StringComparer.OrdinalIgnoreCase);
                issues = issues.Select(issue => issue.IsServiceDesk && previous.TryGetValue(issue.Key, out var sla)
                    ? issue with { Sla = sla } : issue).ToArray();
            }
            _slaOwner = client;
            _lastIssues = issues;
            _pinnedCount = pinnedCount;
            _app.ReportAuthSuccess();
            StatusMessage = warning;
            Rebuild();
            await Task.WhenAll(_app.Worklogs.RetryPendingAsync(), LoadSlasAsync(client, issues));
        }
        catch (JiraApiException ex) when (ex.StatusCode == 401)
        {
            if (!ReferenceEquals(_app.Client, client)) return;
            StatusMessage = client.Connection.IsDataCenter ? ex.Message : "Jira sign-in expired – update your API token in Settings";
            _app.ReportAuthFailure();
        }
        catch (Exception ex)
        {
            Log.Error("Refresh failed", ex);
            StatusMessage = ex is HttpRequestException ? "Can't reach Jira" : ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            PendingWorklogs = _app.Worklogs.PendingCount;
            if (_refreshRequested)
            {
                _refreshRequested = false;
                _ = RefreshAsync();
            }
        }
    }

    private async Task LoadSlasAsync(IJiraClient client, IReadOnlyList<JiraIssue> issues)
    {
        var result = issues.ToArray();
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        await Parallel.ForEachAsync(Enumerable.Range(0, result.Length).Where(i => result[i].IsServiceDesk),
            options, async (index, ct) =>
            {
                if (!ReferenceEquals(client, _app.Client)) return;
                var issue = result[index];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    var sla = await client.GetRequestSlaAsync(issue.Key, timeout.Token);
                    result[index] = issue with { Sla = sla };
                }
                catch (JiraApiException ex) when (ex.StatusCode is 401 or 403 or 404)
                {
                    result[index] = issue with { Sla = null };
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    // A slow request must leave capacity for the next ticket.
                }
                catch (Exception ex) when (ex is JiraApiException or HttpRequestException)
                {
                    Log.Error($"Could not load SLA for {issue.Key}", ex);
                }
            });

        if (!ReferenceEquals(client, _app.Client) || result.SequenceEqual(issues)) return;
        _lastIssues = result;
        Rebuild();
    }

    /// <summary>Re-applies the active state after the tracker changes.</summary>
    public void SyncActive()
    {
        Rebuild();
        Tick();
    }

    /// <summary>Called every second to update the running timer text.</summary>
    public void Tick()
    {
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        if (Filter.UsesCurrentTime && minute != _lastFilterMinute)
        {
            _lastFilterMinute = minute;
            Rebuild();
        }
        var activeKey = _app.Tracker.ActiveKey;
        foreach (var t in Tickets)
        {
            var active = string.Equals(t.Key, activeKey, StringComparison.OrdinalIgnoreCase);
            t.IsActive = active;
            t.RefreshDueDate();
            t.ElapsedText = active ? FormatElapsed(_app.Tracker.Elapsed) : string.Empty;
        }
    }

    public TicketViewModel? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>Re-renders button text after the show-titles setting changes.</summary>
    public void RefreshTitles()
    {
        foreach (var t in Tickets) t.RefreshSubtitle();
    }

    /// <summary>Refreshes commands whose availability depends on the Jira connection.</summary>
    public void RefreshConnectionState()
    {
        foreach (var t in Tickets) t.RefreshConnectionState();
    }

    private void Rebuild()
    {
        var activeKey = _app.Tracker.ActiveKey;
        var now = DateTimeOffset.UtcNow;
        var target = new List<TicketViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiblePins = 0;

        for (var index = 0; index < _lastIssues.Count; index++)
        {
            var issue = _lastIssues[index];
            var isPinned = index < _pinnedCount;
            var vm = GetOrCreate(issue);
            vm.Issue = issue;
            vm.IsStale = false;
            if (!Filter.Matches(issue, now, TimeZoneInfo.Local))
            {
                if (!isPinned || !issue.Key.Equals(activeKey, StringComparison.OrdinalIgnoreCase)) continue;
                vm.IsStale = true;
            }
            target.Add(vm);
            seen.Add(issue.Key);
            if (isPinned) visiblePins++;
        }

        // A running ticket outside the visible list stays in the queue section.
        if (activeKey is not null && !seen.Contains(activeKey))
        {
            var vm = _byKey.GetValueOrDefault(activeKey) ?? GetOrCreate(TicketViewModel.Placeholder(activeKey));
            vm.IsStale = true;
            var index = target.FindIndex(visiblePins, t => t.Issue.PriorityRank > vm.Issue.PriorityRank);
            target.Insert(index < 0 ? target.Count : index, vm);
        }

        // Move/insert in place so existing buttons (and hover state) survive refreshes.
        for (var i = 0; i < target.Count; i++)
        {
            var current = Tickets.IndexOf(target[i]);
            if (current < 0) Tickets.Insert(i, target[i]);
            else if (current != i) Tickets.Move(current, i);
        }
        while (Tickets.Count > target.Count)
            Tickets.RemoveAt(Tickets.Count - 1);

        foreach (var ticket in target)
            _ = LoadTypeIconAsync(ticket);

        foreach (var key in _byKey.Keys.Where(k => !target.Any(t => t.Key.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
            _byKey.Remove(key);
    }

    /// <summary>Shows the issue type's Jira icon (cached per type); the built-in glyph stays if it can't load.</summary>
    private async Task LoadTypeIconAsync(TicketViewModel ticket)
    {
        var avatarId = ticket.Issue.IssueTypeAvatarId;
        var icon = await _app.Icons.GetAsync(avatarId);
        // The issue may have been refreshed (e.g. type changed) while the icon loaded.
        if (ticket.Issue.IssueTypeAvatarId == avatarId) ticket.TypeIcon = icon;
    }

    private TicketViewModel GetOrCreate(JiraIssue issue)
    {
        if (_byKey.TryGetValue(issue.Key, out var existing)) return existing;
        var vm = new TicketViewModel(issue,
            toggle: t => _app.ToggleTimer(t.Key),
            open: t => _app.OpenInBrowser(t.Key),
            comment: t => _app.OpenCommentEditor(t),
            showTitles: () => _app.Settings.ShowTitlesOnBar,
            canComment: () => _app.Client is not null,
            calendar: t => _app.OpenWorklogCalendar(t));
        _byKey[issue.Key] = vm;
        return vm;
    }

    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
}
