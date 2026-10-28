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

    public BarViewModel(AppController app)
    {
        _app = app;
        Tickets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<TicketViewModel> Tickets { get; } = [];

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

    public string EmptyText => IsRefreshing ? "Loading…" : HasStatus ? "⚠ " + StatusMessage : "No tickets match your query";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        if (_app.Client is null)
        {
            StatusMessage = "Not connected – open Settings";
            return;
        }

        IsRefreshing = true;
        try
        {
            _lastIssues = await _app.FetchIssuesAsync();
            _app.ReportAuthSuccess();
            StatusMessage = null;
            Rebuild();
            await _app.Worklogs.RetryPendingAsync();
        }
        catch (JiraApiException ex) when (ex.StatusCode == 401)
        {
            StatusMessage = "Jira sign-in expired – update your API token in Settings";
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
        }
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
        var activeKey = _app.Tracker.ActiveKey;
        foreach (var t in Tickets)
        {
            var active = string.Equals(t.Key, activeKey, StringComparison.OrdinalIgnoreCase);
            t.IsActive = active;
            t.ElapsedText = active ? FormatElapsed(_app.Tracker.Elapsed) : string.Empty;
        }
    }

    public TicketViewModel? Find(string key) => _byKey.GetValueOrDefault(key);

    private void Rebuild()
    {
        var activeKey = _app.Tracker.ActiveKey;
        var target = new List<TicketViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var issue in _lastIssues)
        {
            var vm = GetOrCreate(issue);
            vm.Issue = issue;
            vm.IsStale = false;
            target.Add(vm);
            seen.Add(issue.Key);
        }

        // The running ticket stays on the bar (dimmed) even if it no longer matches the query.
        if (activeKey is not null && !seen.Contains(activeKey))
        {
            var vm = _byKey.GetValueOrDefault(activeKey) ?? GetOrCreate(TicketViewModel.Placeholder(activeKey));
            vm.IsStale = true;
            var index = target.FindIndex(t => t.Issue.PriorityRank > vm.Issue.PriorityRank);
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

        foreach (var key in _byKey.Keys.Where(k => !target.Any(t => t.Key.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
            _byKey.Remove(key);
    }

    private TicketViewModel GetOrCreate(JiraIssue issue)
    {
        if (_byKey.TryGetValue(issue.Key, out var existing)) return existing;
        var vm = new TicketViewModel(issue,
            toggle: t => _app.ToggleTimer(t.Key),
            open: t => _app.OpenInBrowser(t.Key),
            comment: t => _app.OpenCommentEditor(t));
        _byKey[issue.Key] = vm;
        return vm;
    }

    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
}
