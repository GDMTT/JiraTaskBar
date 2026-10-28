using System.Text.Json.Serialization;

namespace JiraTaskBar.Core.Storage;

public enum DockEdge { Floating, Left, Top, Right, Bottom }

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
    public int RefreshMinutes { get; set; } = 5;
    public ShortSessionMode ShortSessions { get; set; } = ShortSessionMode.RoundUpToMinute;
    /// <summary>Prompt to exclude time away (lock/sleep) when the gap is at least this long. 0 disables.</summary>
    public int IdlePromptMinutes { get; set; } = 10;
    public bool StartWithWindows { get; set; }

    public DockEdge Dock { get; set; } = DockEdge.Floating;
    /// <summary>Device name of the monitor the bar was last docked or floating on (e.g. \\.\DISPLAY1).</summary>
    public string? MonitorDevice { get; set; }
    public double? FloatLeft { get; set; }
    public double? FloatTop { get; set; }
}
