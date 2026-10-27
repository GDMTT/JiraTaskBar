using System.Globalization;
using System.Text.Json.Nodes;

namespace JiraTaskBar.Core.Jira;

/// <summary>Builds and parses Jira REST payloads. Kept separate from HTTP so it can be unit tested.</summary>
public static class JiraPayloads
{
    public const string ServiceDeskCommentProperty = "sd.public.comment";

    /// <summary>Jira expects e.g. 2026-10-28T09:12:00.000+1300 (offset without a colon).</summary>
    public static string FormatJiraDate(DateTimeOffset value)
    {
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)
               + sign + abs.Hours.ToString("00", CultureInfo.InvariantCulture)
               + abs.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    public static JsonObject Worklog(DateTimeOffset startedLocal, int timeSpentSeconds, string? comment)
    {
        var body = new JsonObject
        {
            ["started"] = FormatJiraDate(startedLocal),
            ["timeSpentSeconds"] = timeSpentSeconds,
        };
        if (!string.IsNullOrWhiteSpace(comment))
            body["comment"] = PlainTextAdf(comment);
        return body;
    }

    /// <summary>v2 comment body: wiki markup text, optionally flagged internal/public for service desk projects.</summary>
    public static JsonObject Comment(string wikiMarkup, bool? isInternal)
    {
        var body = new JsonObject { ["body"] = wikiMarkup };
        if (isInternal is { } internalFlag)
        {
            body["properties"] = new JsonArray
            {
                new JsonObject
                {
                    ["key"] = ServiceDeskCommentProperty,
                    ["value"] = new JsonObject { ["internal"] = internalFlag },
                },
            };
        }
        return body;
    }

    /// <summary>Minimal Atlassian Document Format document: one paragraph per line.</summary>
    public static JsonObject PlainTextAdf(string text)
    {
        var content = new JsonArray();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var paragraph = new JsonObject { ["type"] = "paragraph" };
            if (line.Length > 0)
                paragraph["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = line } };
            content.Add(paragraph);
        }
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = content };
    }

    /// <summary>Parses /rest/api/3/priority. The array is in the site's configured order, highest first.</summary>
    public static IReadOnlyList<JiraPriority> ParsePriorities(JsonNode? node)
    {
        var array = node as JsonArray ?? node?["values"] as JsonArray ?? [];
        var result = new List<JiraPriority>();
        foreach (var item in array)
        {
            if (item is null) continue;
            var id = item["id"]?.GetValue<string>() ?? string.Empty;
            var name = item["name"]?.GetValue<string>() ?? id;
            result.Add(new JiraPriority(id, name, result.Count + 1));
        }
        return result;
    }

    public static (IReadOnlyList<JiraIssue> Issues, string? NextPageToken) ParseSearch(
        JsonNode? node, IReadOnlyDictionary<string, JiraPriority> prioritiesById)
    {
        var issues = new List<JiraIssue>();
        foreach (var item in node?["issues"] as JsonArray ?? [])
        {
            if (item is null) continue;
            var fields = item["fields"];
            JiraPriority? priority = null;
            var priorityNode = fields?["priority"];
            if (priorityNode is not null)
            {
                var pid = priorityNode["id"]?.GetValue<string>() ?? string.Empty;
                priority = prioritiesById.TryGetValue(pid, out var known)
                    ? known
                    : new JiraPriority(pid, priorityNode["name"]?.GetValue<string>() ?? pid, int.MaxValue - 1);
            }

            issues.Add(new JiraIssue(
                Key: item["key"]?.GetValue<string>() ?? string.Empty,
                Summary: fields?["summary"]?.GetValue<string>() ?? string.Empty,
                Priority: priority,
                Status: fields?["status"]?["name"]?.GetValue<string>() ?? string.Empty,
                ProjectTypeKey: fields?["project"]?["projectTypeKey"]?.GetValue<string>() ?? string.Empty,
                IssueType: fields?["issuetype"]?["name"]?.GetValue<string>() ?? string.Empty));
        }

        var isLast = node?["isLast"]?.GetValue<bool>() ?? true;
        var next = isLast ? null : node?["nextPageToken"]?.GetValue<string>();
        return (issues, next);
    }

    public static string ErrorMessage(JsonNode? node, string fallback)
    {
        var parts = new List<string>();
        foreach (var m in node?["errorMessages"] as JsonArray ?? [])
            if (m is not null) parts.Add(m.GetValue<string>());
        if (node?["errors"] is JsonObject errors)
            foreach (var (field, value) in errors)
                parts.Add($"{field}: {value}");
        return parts.Count > 0 ? string.Join("; ", parts) : fallback;
    }
}
