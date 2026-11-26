using JiraTaskBar.Core.Jira;
using JiraTaskBar.Core.Tracking;

namespace JiraTaskBar.App.Services;

internal sealed record WorklogCalendarLoadResult(
    IReadOnlyList<JiraWorklog> Worklogs,
    IReadOnlyList<JiraIssue> DiscoveredIssues,
    IReadOnlyList<string> IssueKeys,
    int FailedIssueCount,
    string AccountId,
    bool DiscoveryFailed);

internal static class WorklogCalendarData
{
    private sealed record IssueLoad(string Key, IReadOnlyList<JiraWorklog> Worklogs, Exception? Error);

    public static IReadOnlyList<JiraWorklog> ReplaceIssueWorklogs(IReadOnlyList<JiraWorklog> existing,
        string issueKey, IReadOnlyList<JiraWorklog> replacement) => existing
        .Where(worklog => !worklog.IssueKey.Equals(issueKey, StringComparison.OrdinalIgnoreCase))
        .Concat(replacement)
        .ToList();

    public static async Task<WorklogCalendarLoadResult> LoadAsync(IJiraClient client,
        IReadOnlyList<JiraIssue> issues, JiraIssue selected, WorklogCalendarRange range, CancellationToken ct,
        IEnumerable<string>? localIssueKeys = null, Action<WorklogCalendarLoadResult>? showPreview = null)
    {
        var localKeys = localIssueKeys?.ToArray() ?? [];
        var me = await client.GetMyselfAsync(ct);
        if (string.IsNullOrWhiteSpace(me.AccountId))
            throw new InvalidOperationException("Jira did not return the signed-in account ID.");

        var zone = TimeZoneInfo.Local;
        var start = WorklogCalendarLayout.LocalDateTime(range.Start, TimeOnly.MinValue, zone);
        var end = WorklogCalendarLayout.LocalDateTime(range.EndExclusive, TimeOnly.MinValue, zone);
        var previousDay = range.Start.AddDays(-1);
        var split = WorklogCalendarLayout.LocalDateTime(previousDay, TimeOnly.MinValue, zone);
        // Jira may index dates in a different time zone from the calendar.
        var searchUntil = range.EndExclusive.AddDays(2);
        using var requests = new SemaphoreSlim(6);

        var previewLoads = new Dictionary<string, IssueLoad>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<JiraIssue> previewIssues = [];
        if (showPreview is not null)
        {
            var preview = await DiscoverAsync(previousDay.AddDays(-2));
            previewIssues = preview.Issues;
            var recent = await Task.WhenAll(Keys(issues.Append(selected).Concat(previewIssues))
                .Select(key => LoadIssueAsync(key, split, end)));
            foreach (var load in recent.Where(load => load.Error is null)) previewLoads.Add(load.Key, load);
            ct.ThrowIfCancellationRequested();
            showPreview(BuildResult(recent, previewIssues, preview.Failed));
        }

        // Jira filters by start time, so older worklogs can still contribute to this range.
        // Check those after showing the recent entries and reuse successful preview requests.
        var history = await DiscoverAsync(null);
        var discovered = history.Issues.Concat(previewIssues)
            .DistinctBy(issue => issue.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var results = await Task.WhenAll(Keys(issues.Append(selected).Concat(discovered)).Select(CompleteIssueAsync));
        if (results.All(result => result.Error is not null) && results.FirstOrDefault()?.Error is { } error &&
            previewLoads.Count == 0)
            throw error;
        return BuildResult(results, discovered, history.Failed);

        IEnumerable<string> Keys(IEnumerable<JiraIssue> candidates) => candidates.Select(issue => issue.Key).Concat(localKeys)
            .Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase);

        async Task<(IReadOnlyList<JiraIssue> Issues, bool Failed)> DiscoverAsync(DateOnly? from)
        {
            try
            {
                var found = from is { } lowerBound
                    ? await client.FindWorklogIssuesAsync(me.AccountId, lowerBound, searchUntil, ct)
                    : await client.FindWorklogIssuesAsync(me.AccountId, searchUntil, ct);
                return (found, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return ([], true);
            }
        }

        async Task<IssueLoad> CompleteIssueAsync(string key)
        {
            if (!previewLoads.TryGetValue(key, out var recent)) return await LoadIssueAsync(key, null, end);
            if (client.Connection.IsDataCenter) return recent;

            var older = await LoadIssueAsync(key, null, split);
            return older with { Worklogs = older.Worklogs.Where(worklog => worklog.Started < split)
                .Concat(recent.Worklogs.Where(worklog => worklog.Started >= split))
                .DistinctBy(worklog => worklog.Id).ToArray() };
        }

        async Task<IssueLoad> LoadIssueAsync(string key, DateTimeOffset? from, DateTimeOffset until)
        {
            await requests.WaitAsync(ct);
            try
            {
                var worklogs = await client.GetWorklogsAsync(key, from, until, ct);
                return new(key, ForAccount(worklogs, me.AccountId).Where(worklog =>
                    worklog.Started < end && worklog.TimeSpentSeconds > 0 &&
                    (start - worklog.Started).TotalSeconds < worklog.TimeSpentSeconds).ToArray(), null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new(key, [], ex);
            }
            finally
            {
                requests.Release();
            }
        }

        WorklogCalendarLoadResult BuildResult(IssueLoad[] loads, IReadOnlyList<JiraIssue> found, bool discoveryFailed) =>
            new(loads.SelectMany(load => load.Worklogs).ToArray(), found, loads.Select(load => load.Key).ToArray(),
                loads.Count(load => load.Error is not null), me.AccountId, discoveryFailed);
    }

    public static IReadOnlyList<JiraWorklog> ForAccount(IEnumerable<JiraWorklog> worklogs, string? accountId) =>
        worklogs.Where(worklog => accountId is not null && worklog.AuthorAccountId == accountId).ToList();
}
