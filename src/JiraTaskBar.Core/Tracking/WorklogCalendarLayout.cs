namespace JiraTaskBar.Core.Tracking;

public enum WorklogCalendarView { Month, Week, Day }

public sealed record WorklogCalendarRange(DateOnly Start, DateOnly EndExclusive)
{
    public int DayCount => EndExclusive.DayNumber - Start.DayNumber;
}

/// <summary>Pure date/slot calculations shared by the scheduler UI and its tests.</summary>
public static class WorklogCalendarLayout
{
    public static readonly int[] SupportedResolutions = [5, 10, 15, 30, 60];

    public static int NormalizeResolution(int minutes) =>
        SupportedResolutions.Contains(minutes) ? minutes : 30;

    public static WorklogCalendarRange RangeFor(
        WorklogCalendarView view, DateOnly anchor, DayOfWeek firstDayOfWeek)
    {
        return view switch
        {
            WorklogCalendarView.Day => new(anchor, anchor.AddDays(1)),
            WorklogCalendarView.Week => Week(anchor, firstDayOfWeek),
            WorklogCalendarView.Month => new(
                new DateOnly(anchor.Year, anchor.Month, 1),
                new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(view)),
        };
    }

    public static DateOnly Move(DateOnly anchor, WorklogCalendarView view, int direction) => view switch
    {
        WorklogCalendarView.Day => anchor.AddDays(direction),
        WorklogCalendarView.Week => anchor.AddDays(7 * direction),
        WorklogCalendarView.Month => anchor.AddMonths(direction),
        _ => throw new ArgumentOutOfRangeException(nameof(view)),
    };

    public static int SlotIndex(TimeOnly time, int resolutionMinutes)
    {
        var resolution = NormalizeResolution(resolutionMinutes);
        return (time.Hour * 60 + time.Minute) / resolution;
    }

    public static int SlotSpan(int seconds, int resolutionMinutes)
    {
        var slotSeconds = NormalizeResolution(resolutionMinutes) * 60;
        return Math.Max(1, (int)Math.Ceiling(Math.Max(0, seconds) / (double)slotSeconds));
    }

    private static WorklogCalendarRange Week(DateOnly anchor, DayOfWeek firstDayOfWeek)
    {
        var back = (7 + (int)anchor.DayOfWeek - (int)firstDayOfWeek) % 7;
        var start = anchor.AddDays(-back);
        return new WorklogCalendarRange(start, start.AddDays(7));
    }
}
