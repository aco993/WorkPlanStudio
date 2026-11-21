namespace WorkPlanStudio.Components;

/// <summary>How much of a run the Gantt chart shows at once.</summary>
public enum GanttSpan
{
    /// <summary>The whole run across one card.</summary>
    All = 0,

    /// <summary>One calendar month.</summary>
    Month = 1,

    /// <summary>One calendar week, Monday to Sunday.</summary>
    Week = 2,

    /// <summary>One calendar day, midnight to midnight.</summary>
    Day = 3
}

/// <summary>A stretch of the run, in seconds from the horizon. It may start before 0 or end after the makespan.</summary>
/// <param name="StartSeconds">Inclusive start.</param>
/// <param name="EndSeconds">Exclusive end.</param>
public readonly record struct GanttWindow(long StartSeconds, long EndSeconds)
{
    /// <summary>The window's length in seconds.</summary>
    public long Length => EndSeconds - StartSeconds;
}

/// <summary>
/// The calendar windows a run can be read in: days from midnight, weeks from
/// Monday, months from the first. Pure arithmetic, so the chart only has to draw
/// what this decides.
/// <para>
/// The windows follow the wall clock of the horizon, not the run: a run that
/// starts on a Monday at 06:00 has a first day of 00:00 to 24:00, of which the
/// first six hours lie before the run. That is what a planner means by "Monday".
/// </para>
/// </summary>
public static class GanttTimeWindow
{
    private const long DaySeconds = 24 * 3600;

    /// <summary>
    /// The spans worth offering for a run: a span is offered only when the run does
    /// not fit in one of them, because a "week" view of a three-day run is the whole
    /// run again. A month needs a calendar, so it needs a horizon.
    /// </summary>
    public static IReadOnlyList<GanttSpan> Offered(long makespanSeconds, DateTime? horizon)
    {
        var spans = new List<GanttSpan> { GanttSpan.All };
        if (horizon is { } h && makespanSeconds > (long)(h.AddMonths(1) - h).TotalSeconds)
            spans.Add(GanttSpan.Month);
        if (makespanSeconds > 7 * DaySeconds)
            spans.Add(GanttSpan.Week);
        if (makespanSeconds > DaySeconds)
            spans.Add(GanttSpan.Day);
        return spans;
    }

    /// <summary>The windows of a span that overlap the run, in order. <see cref="GanttSpan.All"/> is one window, the run.</summary>
    public static IReadOnlyList<GanttWindow> Windows(GanttSpan span, long makespanSeconds, DateTime? horizon)
    {
        if (span == GanttSpan.All || makespanSeconds <= 0)
            return [new GanttWindow(0, Math.Max(makespanSeconds, 1))];

        var windows = new List<GanttWindow>();
        if (span == GanttSpan.Month)
        {
            // Without a calendar a month has no first day; thirty days is the honest stand-in.
            if (horizon is not { } h)
                return Fixed(30 * DaySeconds, 0, makespanSeconds);

            var month = new DateTime(h.Year, h.Month, 1, 0, 0, 0, h.Kind);
            while (true)
            {
                long start = Seconds(month - h);
                if (start >= makespanSeconds)
                    break;
                var next = month.AddMonths(1);
                windows.Add(new GanttWindow(start, Seconds(next - h)));
                month = next;
            }
            return windows;
        }

        long length = span == GanttSpan.Week ? 7 * DaySeconds : DaySeconds;
        long first = horizon is { } at ? -Seconds(at - PeriodStart(at, span)) : 0;
        return Fixed(length, first, makespanSeconds);
    }

    /// <summary>The index of the window that contains <paramref name="seconds"/>, or the nearest one.</summary>
    public static int IndexOf(IReadOnlyList<GanttWindow> windows, long seconds)
    {
        ArgumentNullException.ThrowIfNull(windows);
        for (var i = 0; i < windows.Count; i++)
        {
            if (seconds < windows[i].EndSeconds)
                return i;
        }
        return Math.Max(0, windows.Count - 1);
    }

    private static List<GanttWindow> Fixed(long length, long first, long makespanSeconds)
    {
        var windows = new List<GanttWindow>();
        for (long start = first; start < makespanSeconds; start += length)
            windows.Add(new GanttWindow(start, start + length));
        return windows;
    }

    private static DateTime PeriodStart(DateTime at, GanttSpan span) => span switch
    {
        GanttSpan.Week => at.Date.AddDays(-(((int)at.DayOfWeek + 6) % 7)),
        _ => at.Date
    };

    private static long Seconds(TimeSpan span) => (long)span.TotalSeconds;
}
