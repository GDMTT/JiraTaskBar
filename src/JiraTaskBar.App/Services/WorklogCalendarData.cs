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
    public static IReadOnlyList<JiraWorklog> ReplaceIssueWorklogs(IReadOnlyList<JiraWorklog> existing,
        string issueKey, IReadOnlyList<JiraWorklog> replacement) => existing
        .Where(worklog => !worklog.IssueKey.Equals(issueKey, StringComparison.OrdinalIgnoreCase))
        .Concat(replacement)
        .ToList();

    public static async Task<WorklogCalendarLoadResult> LoadAsync(IJiraClient client,
        IReadOnlyList<JiraIssue> issues, JiraIssue selected, WorklogCalendarRange range, CancellationToken ct,
        IEnumerable<string>? localIssueKeys = null)
    {
        var localKeys = localIssueKeys?.ToArray() ?? [];
        var me = await client.GetMyselfAsync(ct);
        if (string.IsNullOrWhiteSpace(me.AccountId))
            throw new InvalidOperationException("Jira did not return the signed-in account ID.");

        IReadOnlyList<JiraIssue> discovered = [];
        var discoveryFailed = false;
        try
        {
            // Include overnight starts and account for Jira indexing dates in a different time zone.
            discovered = await client.FindWorklogIssuesAsync(me.AccountId, range.Start.AddDays(-2),
                range.EndExclusive.AddDays(1), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            discoveryFailed = true;
        }

        var keys = issues.Append(selected).Concat(discovered)
            .Select(issue => issue.Key)
            .Concat(localKeys)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        using var requests = new SemaphoreSlim(6);
        var loads = keys.Select(async key =>
        {
            await requests.WaitAsync(ct);
            try
            {
                return (Worklogs: await client.GetWorklogsAsync(key, ct), Error: (Exception?)null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (Worklogs: (IReadOnlyList<JiraWorklog>)[], Error: ex);
            }
            finally
            {
                requests.Release();
            }
        });
        var results = await Task.WhenAll(loads);
        var failures = results.Count(result => result.Error is not null);
        if (failures == results.Length && results.FirstOrDefault().Error is { } error)
            throw error;
        return new WorklogCalendarLoadResult(
            ForAccount(results.SelectMany(result => result.Worklogs), me.AccountId), discovered,
            keys, failures, me.AccountId, discoveryFailed);
    }

    public static IReadOnlyList<JiraWorklog> ForAccount(IEnumerable<JiraWorklog> worklogs, string? accountId) =>
        worklogs.Where(worklog => accountId is not null && worklog.AuthorAccountId == accountId).ToList();
}
