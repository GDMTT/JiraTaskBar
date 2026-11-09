using System.Globalization;
using System.Text;
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

    public static JsonObject Worklog(DateTimeOffset startedLocal, int timeSpentSeconds, string? comment,
        JsonNode? commentDocument = null)
    {
        var body = new JsonObject
        {
            ["started"] = FormatJiraDate(startedLocal),
            ["timeSpentSeconds"] = timeSpentSeconds,
        };
        if (commentDocument is not null)
            body["comment"] = commentDocument.DeepClone();
        else if (!string.IsNullOrWhiteSpace(comment))
            body["comment"] = PlainTextAdf(comment);
        return body;
    }

    /// <summary>
    /// v2 comment body: wiki markup text plus the Service Management visibility flag.
    /// The flag is always sent: a JSM comment without it is public (emailed to the customer),
    /// so omitting it would fail open. Other project types ignore it.
    /// </summary>
    public static JsonObject Comment(string wikiMarkup, bool isInternal) => new()
    {
        ["body"] = wikiMarkup,
        ["properties"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = ServiceDeskCommentProperty,
                ["value"] = new JsonObject { ["internal"] = isInternal },
            },
        },
    };

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
                IssueType: fields?["issuetype"]?["name"]?.GetValue<string>() ?? string.Empty,
                IssueTypeAvatarId: AvatarId(fields?["issuetype"]),
                CreatedAt: DateTimeOffset.TryParse(fields?["created"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var created) ? created : null,
                DueDate: DateOnly.TryParseExact(fields?["duedate"]?.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var due) ? due : null));
        }

        var isLast = node?["isLast"]?.GetValue<bool>() ?? true;
        var next = isLast ? null : node?["nextPageToken"]?.GetValue<string>();
        return (issues, next);
    }

    public static (IReadOnlyList<JiraWorklog> Worklogs, int Total, int ReturnedCount) ParseWorklogs(JsonNode? node, string issueKey)
    {
        var result = new List<JiraWorklog>();
        var items = node?["worklogs"] as JsonArray ?? [];
        foreach (var item in items)
        {
            if (item is null) continue;
            var startedText = item["started"]?.GetValue<string>();
            if (!DateTimeOffset.TryParse(startedText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var started)) continue;
            result.Add(new JiraWorklog(
                item["id"]?.GetValue<string>() ?? string.Empty,
                issueKey,
                started,
                item["timeSpentSeconds"]?.GetValue<int>() ?? 0,
                item["author"]?["displayName"]?.GetValue<string>() ?? string.Empty,
                AdfText(item["comment"]),
                item["comment"]?.DeepClone()));
        }
        return (result, node?["total"]?.GetValue<int>() ?? items.Count, items.Count);
    }

    private static string? AdfText(JsonNode? node)
    {
        if (node is null) return null;
        var textBuilder = new StringBuilder();
        CollectText(node, textBuilder);
        var text = textBuilder.ToString().TrimEnd();
        return text.Length == 0 ? null : text;
    }

    private static void CollectText(JsonNode node, StringBuilder text)
    {
        if (node is JsonObject obj)
        {
            var type = obj["type"]?.GetValue<string>();
            if (type == "text" && obj["text"]?.GetValue<string>() is { } inline)
            {
                text.Append(inline);
                return;
            }
            if (type == "hardBreak")
            {
                text.AppendLine();
                return;
            }
            if (obj["content"] is JsonArray content)
                foreach (var child in content)
                    if (child is not null) CollectText(child, text);

            // Inline text fragments are deliberately concatenated exactly. Only ADF block nodes
            // introduce line boundaries; formatting marks may split a word across text nodes.
            if (type is "paragraph" or "heading" or "codeBlock" or "blockquote" or "listItem" &&
                text.Length > 0 && text[^1] != '\n')
                text.AppendLine();
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var plain))
        {
            // Jira v2/server responses may contain a plain string rather than ADF.
            text.Append(plain);
        }
    }

    /// <summary>
    /// The issue type's avatar ID: the "avatarId" field (a number), or the ID inside its
    /// ".../universal_avatar/view/type/issuetype/avatar/{id}" icon URL. Only digits are accepted.
    /// </summary>
    public static string? AvatarId(JsonNode? issueType)
    {
        if (issueType is null) return null;
        var direct = issueType["avatarId"]?.ToJsonString().Trim('"');
        if (IsAvatarId(direct)) return direct;

        var iconUrl = issueType["iconUrl"]?.GetValue<string>();
        const string marker = "/avatar/";
        var at = iconUrl?.LastIndexOf(marker, StringComparison.Ordinal) ?? -1;
        if (at < 0) return null;
        var tail = iconUrl![(at + marker.Length)..];
        var id = new string(tail.TakeWhile(char.IsAsciiDigit).ToArray());
        return IsAvatarId(id) ? id : null;
    }

    public static bool IsAvatarId(string? value) =>
        value is { Length: > 0 and <= 18 } && value.All(char.IsAsciiDigit);

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
