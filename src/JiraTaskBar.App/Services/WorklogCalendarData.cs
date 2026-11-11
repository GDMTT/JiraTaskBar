using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.Services;

internal sealed record WorklogCalendarLoadResult(
    IReadOnlyList<JiraWorklog> Worklogs,
    IReadOnlyList<string> IssueKeys,
    int FailedIssueCount);

internal static class WorklogCalendarData
{
    public static async Task<WorklogCalendarLoadResult> LoadAsync(IJiraClient client,
        IReadOnlyList<JiraIssue> issues, JiraIssue selected, CancellationToken ct)
    {
        var keys = issues.Append(selected)
            .Select(issue => issue.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var loads = keys.Select(async key =>
        {
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
        });
        var results = await Task.WhenAll(loads);
        var failures = results.Count(result => result.Error is not null);
        if (failures == results.Length && results.FirstOrDefault().Error is { } error)
            throw error;
        return new WorklogCalendarLoadResult(
            results.SelectMany(result => result.Worklogs).ToList(), keys, failures);
    }
}
