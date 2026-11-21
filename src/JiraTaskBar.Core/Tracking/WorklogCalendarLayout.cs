namespace JiraTaskBar.Core.Tracking;

public enum WorklogCalendarView { Month, Week, Day }

public sealed record WorklogCalendarRange(DateOnly Start, DateOnly EndExclusive)
{
    public int DayCount => EndExclusive.DayNumber - Start.DayNumber;
}

public readonly record struct WorklogCalendarHours(int StartMinute, int EndMinute)
{
    public static WorklogCalendarHours AllDay => new(0, 24 * 60);

    public static bool IsValid(int start, int end) => start >= 0 && start < end && end <= 24 * 60;

    public static WorklogCalendarHours FromSettings(int? start, int? end) =>
        start is { } first && end is { } last && IsValid(first, last) ? new(first, last) : new(9 * 60, 17 * 60);

    public int FirstSlot(int resolution) => StartMinute / resolution;
    public int EndSlot(int resolution) => (EndMinute + resolution - 1) / resolution;
    public int StartOfSlot(int slot, int resolution) => Math.Max(slot * resolution, StartMinute);
    public int EndOfSlot(int slot, int resolution) => Math.Min((slot + 1) * resolution, EndMinute);
    public bool Contains(double minute) => minute >= StartMinute && minute < EndMinute;

    public static DateTimeOffset Boundary(DateOnly date, int minute, TimeZoneInfo zone) =>
        WorklogCalendarLayout.LocalDateTime(date.AddDays(minute / (24 * 60)),
            TimeOnly.MinValue.AddMinutes(minute % (24 * 60)), zone);

    public IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> IntervalsForDay(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        var localStart = midnight.AddMinutes(StartMinute);
        var localEnd = midnight.AddMinutes(EndMinute);
        var dayStart = Boundary(date, 0, zone);
        var dayEnd = Boundary(date, 24 * 60, zone);
        var startOffset = zone.GetUtcOffset(dayStart);
        var endOffset = zone.GetUtcOffset(dayEnd.AddTicks(-1));
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>(2);

        if (startOffset == endOffset)
            AddInterval(dayStart, dayEnd, startOffset);
        else
        {
            // Intersect the wall-clock hours with each side of the clock change.
            // In a repeated hour these intersections may be separate intervals.
            var transition = FindOffsetChange(dayStart, dayEnd, startOffset, zone);
            AddInterval(dayStart, transition, startOffset);
            AddInterval(transition, dayEnd, endOffset);
        }
        return intervals;

        void AddInterval(DateTimeOffset from, DateTimeOffset until, TimeSpan offset)
        {
            var start = new DateTimeOffset(localStart, offset);
            var end = new DateTimeOffset(localEnd, offset);
            if (start < from) start = from;
            if (end > until) end = until;
            if (start >= end) return;
            if (intervals.Count > 0 && intervals[^1].End == start)
                intervals[^1] = (intervals[^1].Start, end);
            else intervals.Add((start, end));
        }
    }

    public DateTimeOffset? FindMoveStart(DateOnly date, int requestedSlot, int durationSeconds, int resolution,
        TimeZoneInfo zone)
    {
        var intervals = IntervalsForDay(date, zone);
        if (!intervals.Any(interval => (interval.End - interval.Start).TotalSeconds >= durationSeconds)) return null;
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        var lastCandidate = Math.Min(requestedSlot, EndSlot(resolution) - 1);
        for (var slot = lastCandidate; slot >= FirstSlot(resolution); slot--)
        {
            var local = midnight.AddMinutes(StartOfSlot(slot, resolution));
            if (zone.IsInvalidTime(local)) continue;
            IEnumerable<TimeSpan> offsets = zone.IsAmbiguousTime(local)
                ? zone.GetAmbiguousTimeOffsets(local).OrderDescending() : [zone.GetUtcOffset(local)];
            foreach (var offset in offsets)
            {
                var start = new DateTimeOffset(local, offset);
                var end = start.AddSeconds(durationSeconds);
                if (intervals.Any(interval => start >= interval.Start && end <= interval.End)) return start;
            }
        }
        return null;
    }

    private static DateTimeOffset FindOffsetChange(DateTimeOffset start, DateTimeOffset end, TimeSpan offset,
        TimeZoneInfo zone)
    {
        var low = start.UtcTicks;
        var high = end.UtcTicks;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (zone.GetUtcOffset(new DateTimeOffset(middle, TimeSpan.Zero)) == offset) low = middle;
            else high = middle;
        }
        return new DateTimeOffset(high, TimeSpan.Zero);
    }
}

/// <summary>Pure date/slot calculations shared by the scheduler UI and its tests.</summary>
public static class WorklogCalendarLayout
{
    public static readonly int[] SupportedResolutions = [5, 10, 15, 30, 60];
    private static readonly IReadOnlySet<DayOfWeek> DefaultWorkdays = new HashSet<DayOfWeek>
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

    public static IReadOnlySet<DayOfWeek> Workdays(IEnumerable<DayOfWeek>? configured)
    {
        var days = configured?.Where(Enum.IsDefined).ToHashSet();
        return days is { Count: > 0 } ? days : DefaultWorkdays;
    }

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
        var result = new (int Lane, int LaneCount)[intervals.Count];
        var group = new List<(int Index, int Lane)>();
        var laneEnds = new List<int>();
        var groupEnd = int.MinValue;
        void FinishGroup()
        {
            foreach (var item in group) result[item.Index] = (item.Lane, laneEnds.Count);
            group.Clear();
            laneEnds.Clear();
        }
        foreach (var item in intervals.Select((value, index) => (value, index))
                     .OrderBy(x => x.value.StartSlot).ThenBy(x => x.value.EndSlot))
        {
            if (item.value.StartSlot >= groupEnd) FinishGroup();
            var lane = laneEnds.FindIndex(end => end <= item.value.StartSlot);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(item.value.EndSlot);
            }
            else laneEnds[lane] = item.value.EndSlot;
            group.Add((item.index, lane));
            groupEnd = Math.Max(groupEnd, item.value.EndSlot);
        }
        FinishGroup();
        return result;
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
