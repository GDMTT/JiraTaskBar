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

    /// <summary>
    /// Maps an elapsed worklog duration onto a wall-clock grid.  Spring-forward gaps need
    /// extra rows, while repeated autumn hours cannot be represented twice in a 24-hour grid;
    /// in that case the elapsed span is retained so the entry does not collapse.
    /// </summary>
    public static int DisplaySlotSpan(DateTimeOffset started, int seconds, int resolutionMinutes,
        TimeZoneInfo timeZone)
    {
        var resolution = NormalizeResolution(resolutionMinutes);
        var localStart = TimeZoneInfo.ConvertTime(started, timeZone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(started.AddSeconds(seconds), timeZone).DateTime;
        var wallClockMinutes = Math.Max(0, (localEnd - localStart).TotalMinutes);
        var elapsedMinutes = Math.Max(0, seconds / 60d);
        return Math.Max(1, (int)Math.Ceiling(Math.Max(wallClockMinutes, elapsedMinutes) / resolution));
    }

    /// <summary>Returns the elapsed duration represented by local grid boundaries.</summary>
    public static int SelectionSeconds(DateOnly date, int startSlot, int slotCount,
        int resolutionMinutes, TimeZoneInfo timeZone)
    {
        var resolution = NormalizeResolution(resolutionMinutes);
        var slotsPerDay = 24 * 60 / resolution;
        var startMinutes = Math.Clamp(startSlot, 0, slotsPerDay - 1) * resolution;
        var endMinutes = Math.Clamp(startSlot + Math.Max(1, slotCount), 1, slotsPerDay) * resolution;
        var start = LocalDateTime(date, TimeOnly.MinValue.AddMinutes(startMinutes), timeZone);
        var endDate = endMinutes == 24 * 60 ? date.AddDays(1) : date;
        var endTime = endMinutes == 24 * 60 ? TimeOnly.MinValue : TimeOnly.MinValue.AddMinutes(endMinutes);
        var end = LocalDateTime(endDate, endTime, timeZone);
        return Math.Max(60, (int)Math.Round((end.ToUniversalTime() - start.ToUniversalTime()).TotalSeconds));
    }

    /// <summary>Assigns overlapping slot ranges to side-by-side lanes.</summary>
    public static IReadOnlyList<(int Lane, int LaneCount)> AssignOverlapLanes(
        IReadOnlyList<(int StartSlot, int EndSlot)> intervals)
    {
        if (intervals.Count == 0) return [];
        var laneEnds = new List<int>();
        var lanes = new int[intervals.Count];
        foreach (var item in intervals.Select((value, index) => (value, index))
                     .OrderBy(x => x.value.StartSlot).ThenBy(x => x.value.EndSlot))
        {
            var lane = laneEnds.FindIndex(end => end <= item.value.StartSlot);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(item.value.EndSlot);
            }
            else laneEnds[lane] = item.value.EndSlot;
            lanes[item.index] = lane;
        }
        return lanes.Select(lane => (lane, laneEnds.Count)).ToList();
    }

    /// <summary>Resolves a local calendar time, choosing the earliest instant if it repeats.</summary>
    public static DateTimeOffset LocalDateTime(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static WorklogCalendarRange Week(DateOnly anchor, DayOfWeek firstDayOfWeek)
    {
        var back = (7 + (int)anchor.DayOfWeek - (int)firstDayOfWeek) % 7;
        var start = anchor.AddDays(-back);
        return new WorklogCalendarRange(start, start.AddDays(7));
    }
}
