using System.Text.Json.Serialization;

namespace JiraTaskBar.Core.Storage;

public enum DockEdge { Floating, Left, Top, Right, Bottom }
public enum TicketGrouping { None, Priority, Type, Project }
public enum TicketSort { Priority, ResolutionSla, Number }
public sealed record WorklogTypeColor(string Project, string IssueType, string Color);

public enum ShortSessionMode
{
    /// <summary>Sessions under a minute are not logged.</summary>
    Skip,
    /// <summary>Sessions under a minute are logged as one minute (Jira's minimum).</summary>
    RoundUpToMinute,
}

/// <summary>
/// Non-secret preferences. The Jira site and email live in the encrypted <see cref="StoredCredentials"/>,
/// not here, so tampering with this file can't redirect the API token.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultJql = "assignee = currentUser() AND statusCategory != Done ORDER BY priority DESC, key ASC";

    /// <summary>Only read to migrate a v1 install into <see cref="StoredCredentials"/>; never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SiteUrl { get; set; }

    /// <summary>Only read to migrate a v1 install into <see cref="StoredCredentials"/>; never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }

    public string Jql { get; set; } = DefaultJql;
    public Dictionary<string, string[]> AdditionalTicketKeysBySite { get; set; } = new(StringComparer.Ordinal);
    public int RefreshMinutes { get; set; } = 5;
    public ShortSessionMode ShortSessions { get; set; } = ShortSessionMode.RoundUpToMinute;
    /// <summary>Prompt to exclude time away (lock/sleep) when the gap is at least this long. 0 disables.</summary>
    public int IdlePromptMinutes { get; set; } = 10;
    /// <summary>Minute interval used to divide and snap the Day/Week worklog calendar.</summary>
    public int CalendarResolutionMinutes { get; set; } = 30;
    public DayOfWeek? CalendarFirstDayOfWeek { get; set; }
    public DayOfWeek[]? CalendarWorkdays { get; set; }
    public int CalendarWorkStartMinute { get; set; } = 9 * 60;
    public int CalendarWorkEndMinute { get; set; } = 17 * 60;
    public bool ShowOnlyCalendarWorkdays { get; set; }
    public bool ShowOnlyCalendarWorkHours { get; set; }
    public int? DailyWorklogTargetMinutes { get; set; }
    public List<WorklogTypeColor> WorklogTypeColors { get; set; } = [];
    public bool StartWithWindows { get; set; }

    /// <summary>Exclude the bar (and its tooltips/menus) from screen sharing, screenshots and recordings.</summary>
    public bool HideFromScreenCapture { get; set; } = true;

    public bool ShowTitlesOnBar { get; set; } = true;
    public bool ShowTicketNumbersOnBar { get; set; } = true;
    public bool ShowPriorityIconOnBar { get; set; } = true;
    public bool ShowTypeIconOnBar { get; set; } = true;
    public TicketGrouping TicketGrouping { get; set; }
    public TicketSort TicketSort { get; set; }
    public bool SortTicketsDescending { get; set; }

    [JsonIgnore]
    public bool HasTicketGrouping => TicketGrouping is TicketGrouping.Priority or TicketGrouping.Type or TicketGrouping.Project;

    [JsonIgnore]
    public TicketSort EffectiveTicketSort => TicketGrouping == TicketGrouping.Priority && TicketSort == TicketSort.Priority
        ? TicketSort.Number : TicketSort;

    public DockEdge Dock { get; set; } = DockEdge.Floating;
    /// <summary>Device name of the monitor the bar was last docked or floating on (e.g. \\.\DISPLAY1).</summary>
    public string? MonitorDevice { get; set; }
    public double? FloatLeft { get; set; }
    public double? FloatTop { get; set; }
}
