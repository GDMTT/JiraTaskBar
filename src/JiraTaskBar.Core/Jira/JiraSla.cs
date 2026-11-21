using System.Text.Json.Nodes;

namespace JiraTaskBar.Core.Jira;

public sealed record JiraSla(string Name, bool Completed, bool Breached, bool Paused,
    long? GoalMilliseconds, long? RemainingMilliseconds)
{
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset? BreachAt { get; init; }

    internal static JiraSla? Parse(JsonObject metric)
    {
        var ongoing = metric["ongoingCycle"] as JsonObject;
        var cycle = ongoing ?? (metric["completedCycles"] as JsonArray)?.OfType<JsonObject>()
            .Reverse().MaxBy(item => ReadNumber(item, "stopTime", "epochMillis"));
        if (cycle is null || ReadBoolean(cycle, "breached") is not { } breached) return null;

        var name = metric["name"] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text) ? text : "Service level agreement";
        var paused = ongoing is not null &&
            (ReadBoolean(cycle, "paused") == true || ReadBoolean(cycle, "withinCalendarHours") == false);
        var goal = ReadNumber(cycle, "goalDuration");
        var remaining = ReadNumber(cycle, "remainingTime");
        if (remaining is null && goal is > 0 and var goalMillis && ReadNumber(cycle, "elapsedTime") is >= 0 and var elapsed)
            remaining = goalMillis - elapsed;
        return new(name, ongoing is null, breached, paused, goal, remaining)
        {
            BreachAt = ReadInstant(cycle, "breachTime"),
        };
    }

    private static DateTimeOffset? ReadInstant(JsonObject node, string field)
    {
        return ReadNumber(node, field, "epochMillis") is { } epoch &&
            epoch >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() &&
            epoch <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : null;
    }

    private static long? ReadNumber(JsonObject node, string field, string key = "millis") =>
        (node[field] as JsonObject)?[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static bool? ReadBoolean(JsonObject node, string field) =>
        node[field] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
