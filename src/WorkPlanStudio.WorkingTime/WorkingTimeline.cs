using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.WorkingTime;

/// <summary>What a stretch of calendar time is, from the machine's point of view.</summary>
public enum SegmentKind
{
    /// <summary>The crew is at the machine.</summary>
    Working,

    /// <summary>A §4 break inside a shift.</summary>
    Break,

    /// <summary>Between shifts, or an unstaffed day.</summary>
    OffShift,

    /// <summary>A shift start delayed to honour the §5 rest period.</summary>
    Rest,

    /// <summary>Closed by §9: Sunday.</summary>
    Sunday,

    /// <summary>Closed by §9: a public holiday (the label names it).</summary>
    Holiday,

    /// <summary>Closed by an absence (the label names it).</summary>
    Absence
}

/// <summary>A contiguous stretch of the timeline with one kind.</summary>
/// <param name="Start">Inclusive start, wall-clock.</param>
/// <param name="End">Exclusive end.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Label">The shift key, holiday key or absence label — whatever names the cause.</param>
public sealed record TimelineSegment(DateTime Start, DateTime End, SegmentKind Kind, string Label)
{
    /// <summary>Length of the segment.</summary>
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// One place where a rule changed the plan: which rule, which shift, which day,
/// and by how much. The UI turns these into localised sentences ("§ 3: the day
/// shift on Monday was cut from 11 h to 10 h").
/// </summary>
/// <param name="Rule">The rule that fired.</param>
/// <param name="ShiftKey">The shift it applied to.</param>
/// <param name="Day">The weekday the shift starts on.</param>
/// <param name="Before">The value before the rule: a working duration, a rest gap, …</param>
/// <param name="After">The value after the rule was applied.</param>
public sealed record RuleApplication(WorkingTimeRuleId Rule, string ShiftKey, DayOfWeek Day, TimeSpan Before, TimeSpan After)
{
    /// <summary>The section this rule is written in.</summary>
    public string LegalReference =>
        WorkingTimeRules.Catalog.First(r => r.Id == Rule).LegalReference;
}

/// <summary>
/// The engine's view of a timeline: a repeating weekly calendar with a phase,
/// plus the finite list of exceptions — ready to drop onto a <see cref="MachineCapacity"/>.
/// </summary>
/// <param name="Windows">Open windows within one week, sorted, relative to Monday 00:00.</param>
/// <param name="PeriodSeconds">One week.</param>
/// <param name="PhaseSeconds">Where in the week the horizon falls.</param>
/// <param name="Blackouts">Holidays and absences, relative to the horizon, sorted and merged.</param>
/// <param name="MaxBridgeableGapSeconds">The longest closed gap an operation may pause across — the longest §4 break.</param>
public sealed record MachineCalendar(
    IReadOnlyList<CapacityWindow> Windows,
    long PeriodSeconds,
    long PhaseSeconds,
    IReadOnlyList<CapacityBlackout> Blackouts,
    long MaxBridgeableGapSeconds)
{
    /// <summary>The longest operation that can ever be placed on this calendar, in seconds.</summary>
    public long LongestPlacementSeconds => ApplyTo(new MachineCapacity(0, "")).LongestPlacementSeconds;

    /// <summary>Applies this calendar to a work center.</summary>
    public MachineCapacity ApplyTo(MachineCapacity machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine with
        {
            AvailabilityWindows = Windows,
            CalendarPeriodSeconds = Windows.Count == 0 ? 0 : PeriodSeconds,
            CalendarPhaseSeconds = Windows.Count == 0 ? 0 : PhaseSeconds,
            MaxBridgeableGapSeconds = Windows.Count == 0 ? 0 : MaxBridgeableGapSeconds,
            Blackouts = Blackouts
        };
    }
}

/// <summary>
/// A work center's availability over a range of dates, with the reason for
/// every closed stretch and every rule that shaped it. Built once by
/// <see cref="WorkingTimelineBuilder"/> and read by both the scheduler (through
/// <see cref="ToMachineCalendar"/>) and the UI (through <see cref="Segments"/>).
/// </summary>
public sealed class WorkingTimeline
{
    private const long WeekSeconds = 7 * 24 * 3600;

    internal WorkingTimeline(
        ShiftPattern pattern,
        WorkingTimeRules rules,
        DateTime from,
        DateTime to,
        IReadOnlyList<WeekWindow> weekWindows,
        IReadOnlyList<WeekWindow> weekGaps,
        IReadOnlyList<TimelineSegment> exceptions,
        IReadOnlyList<RuleApplication> applications,
        IReadOnlyList<PublicHoliday> holidays,
        int freeSundaysPerYear)
    {
        Pattern = pattern;
        Rules = rules;
        From = from;
        To = to;
        WeekWindows = weekWindows;
        WeekGaps = weekGaps;
        Exceptions = exceptions;
        Applications = applications;
        Holidays = holidays;
        FreeSundaysPerYear = freeSundaysPerYear;
    }

    /// <summary>The pattern this was built from.</summary>
    public ShiftPattern Pattern { get; }

    /// <summary>The rules this was built with.</summary>
    public WorkingTimeRules Rules { get; }

    /// <summary>Start of the covered range.</summary>
    public DateTime From { get; }

    /// <summary>End of the covered range (exclusive). Exceptions are materialised up to here.</summary>
    public DateTime To { get; }

    /// <summary>Open windows within one week, relative to Monday 00:00. Empty for a continuous pattern.</summary>
    public IReadOnlyList<WeekWindow> WeekWindows { get; }

    /// <summary>Closed stretches within one week and why, relative to Monday 00:00.</summary>
    public IReadOnlyList<WeekWindow> WeekGaps { get; }

    /// <summary>Holidays and absences inside the range, sorted and non-overlapping.</summary>
    public IReadOnlyList<TimelineSegment> Exceptions { get; }

    /// <summary>Every place a rule cut, delayed or split a shift in the weekly pattern.</summary>
    public IReadOnlyList<RuleApplication> Applications { get; }

    /// <summary>The holidays that closed the plant inside the range.</summary>
    public IReadOnlyList<PublicHoliday> Holidays { get; }

    /// <summary>How many Sundays a year the pattern leaves free (52 when Sundays are never worked).</summary>
    public int FreeSundaysPerYear { get; }

    /// <summary>
    /// True when the pattern breaks §11: a staffed pattern leaves fewer free
    /// Sundays than the rules demand. A continuous (unattended) pattern never does.
    /// </summary>
    public bool ViolatesFreeSundays => !Pattern.IsContinuous && FreeSundaysPerYear < Rules.MinimumFreeSundaysPerYear;

    /// <summary>Seconds of working time in an ordinary week.</summary>
    public long WeeklyWorkingSeconds =>
        WeekWindows.Count == 0 ? WeekSeconds : WeekWindows.Sum(w => w.EndSeconds - w.StartSeconds);

    /// <summary>
    /// The scheduler's view, with second 0 at <paramref name="horizon"/>. Exceptions
    /// before the horizon are dropped, those after <see cref="To"/> do not exist.
    /// </summary>
    public MachineCalendar ToMachineCalendar(DateTime horizon)
    {
        long phase = WeekOffsetSeconds(horizon);

        var windows = WeekWindows
            .Select(w => new CapacityWindow(w.StartSeconds, w.EndSeconds))
            .ToList();

        var blackouts = new List<CapacityBlackout>();
        foreach (var exception in Exceptions)
        {
            long start = Math.Max(0, Seconds(exception.Start, horizon));
            long end = Seconds(exception.End, horizon);
            if (end <= 0)
                continue;
            blackouts.Add(new CapacityBlackout(start, end, exception.Label));
        }

        // Breaks are bridged (the machine waits for the crew); shift ends, rest
        // periods and closed days are not. The longest break is the threshold.
        long bridge = (long)Math.Max(Rules.BreakAfterNineHours.TotalSeconds, Rules.BreakAfterSixHours.TotalSeconds);
        return new MachineCalendar(windows, WeekSeconds, windows.Count == 0 ? 0 : phase, blackouts, bridge);
    }

    /// <summary>
    /// The annotated timeline between two moments: the weekly pattern
    /// materialised across the dates, with holidays and absences overlaid.
    /// Contiguous, sorted, no gaps.
    /// </summary>
    public IReadOnlyList<TimelineSegment> Segments(DateTime from, DateTime to)
    {
        if (to <= from)
            return [];

        var raw = new List<TimelineSegment>();

        if (WeekWindows.Count == 0)
        {
            raw.Add(new TimelineSegment(from, to, SegmentKind.Working, ""));
        }
        else
        {
            // Walk week by week from the Monday on or before `from`.
            var weekStart = from.Date.AddDays(-WeekdayIndex(from.DayOfWeek));
            var pieces = WeekWindows.Select(w => (w, SegmentKind.Working)).Concat(WeekGaps.Select(g => (g, g.Kind)))
                .OrderBy(p => p.Item1.StartSeconds)
                .ToList();

            while (weekStart < to)
            {
                foreach (var (window, kind) in pieces)
                {
                    var start = weekStart.AddSeconds(window.StartSeconds);
                    var end = weekStart.AddSeconds(window.EndSeconds);
                    if (end <= from || start >= to)
                        continue;
                    raw.Add(new TimelineSegment(Max(start, from), Min(end, to), kind, window.Label));
                }

                weekStart = weekStart.AddDays(7);
            }
        }

        // Overlay exceptions: whatever they cover is replaced.
        var result = new List<TimelineSegment>();
        foreach (var segment in raw)
        {
            var cursor = segment.Start;
            foreach (var exception in Exceptions)
            {
                if (exception.End <= cursor || exception.Start >= segment.End)
                    continue;
                if (exception.Start > cursor)
                    result.Add(segment with { Start = cursor, End = exception.Start });
                cursor = Max(cursor, Min(exception.End, segment.End));
            }

            if (cursor < segment.End)
                result.Add(segment with { Start = cursor });
        }

        // Add the exception segments themselves, clipped to the range, then merge.
        foreach (var exception in Exceptions)
        {
            if (exception.End <= from || exception.Start >= to)
                continue;
            result.Add(exception with { Start = Max(exception.Start, from), End = Min(exception.End, to) });
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return Merge(result);
    }

    /// <summary>Seconds from Monday 00:00 of the week containing <paramref name="moment"/>.</summary>
    public static long WeekOffsetSeconds(DateTime moment) =>
        WeekdayIndex(moment.DayOfWeek) * 24L * 3600 + (long)moment.TimeOfDay.TotalSeconds;

    internal static int WeekdayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static long Seconds(DateTime moment, DateTime horizon) => (long)(moment - horizon).TotalSeconds;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static List<TimelineSegment> Merge(List<TimelineSegment> segments)
    {
        var merged = new List<TimelineSegment>();
        foreach (var segment in segments)
        {
            if (segment.End <= segment.Start)
                continue;
            if (merged.Count > 0 && merged[^1].End == segment.Start && merged[^1].Kind == segment.Kind && merged[^1].Label == segment.Label)
                merged[^1] = merged[^1] with { End = segment.End };
            else
                merged.Add(segment);
        }

        return merged;
    }
}

/// <summary>A stretch within one week, relative to Monday 00:00, with its kind and cause.</summary>
/// <param name="StartSeconds">Seconds after Monday 00:00.</param>
/// <param name="EndSeconds">Exclusive end.</param>
/// <param name="Kind">Working for an open window; otherwise why it is closed.</param>
/// <param name="Label">The shift key that owns it, or the closing cause.</param>
/// <param name="Day">
/// For a working window: the weekday the shift instance <i>started</i> on, so
/// the tail of a Sunday night shift is still Sunday's work. For a gap: the
/// weekday it starts on.
/// </param>
public sealed record WeekWindow(long StartSeconds, long EndSeconds, SegmentKind Kind, string Label, DayOfWeek Day)
{
    /// <summary>Length in seconds.</summary>
    public long DurationSeconds => EndSeconds - StartSeconds;
}
