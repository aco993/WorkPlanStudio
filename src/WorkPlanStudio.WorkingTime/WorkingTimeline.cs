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

/// <summary>Where a pattern stands on the §11 (1) free Sundays.</summary>
public enum SundayRestStatus
{
    /// <summary>An unattended machine: §11 protects people, and there are none on this pattern.</summary>
    NotApplicable,

    /// <summary>No crew works a Sunday at all.</summary>
    Intact,

    /// <summary>Sundays are worked, and the declared rota still leaves at least the required number free.</summary>
    Rotated,

    /// <summary>Sundays are worked and too few stay free — with no rota declared, a crew works every one of them.</summary>
    Breach
}

/// <summary>
/// A contiguous stretch of the timeline with one kind. Both ends are plant-local
/// wall clock (<see cref="PlantTime"/>), so <see cref="Duration"/> is a clock
/// difference — which is elapsed time on every day but the two the clocks change.
/// </summary>
/// <param name="Start">Inclusive start, wall-clock.</param>
/// <param name="End">Exclusive end.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Label">The shift key, holiday key or absence label — whatever names the cause.</param>
public sealed record TimelineSegment(DateTime Start, DateTime End, SegmentKind Kind, string Label)
{
    /// <summary>
    /// Every cause that closed this stretch, not just the first. Overlapping
    /// exceptions merge into one segment, and the causes have to survive that:
    /// the Gantt tooltip is the only place a planner learns that the Thursday is
    /// both a holiday and a maintenance stop.
    /// </summary>
    public IReadOnlyList<string> Causes { get; init; } = [Label];

    /// <summary>Length of the segment on the plant's clock.</summary>
    public TimeSpan Duration => End - Start;

    /// <inheritdoc />
    public bool Equals(TimelineSegment? other) =>
        other is not null && Start == other.Start && End == other.End && Kind == other.Kind
        && string.Equals(Label, other.Label, StringComparison.Ordinal) && Causes.SequenceEqual(other.Causes, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Start);
        hash.Add(End);
        hash.Add(Kind);
        hash.Add(Label, StringComparer.Ordinal);
        foreach (var cause in Causes)
            hash.Add(cause, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>
/// One place where a rule changed the plan, or where a permission the plan uses
/// carries an obligation with it: which rule, which shift, which day, and by how
/// much. The UI turns these into localised sentences ("§ 3: the day shift on
/// Monday was cut from 11 h to 10 h").
/// </summary>
/// <param name="Rule">The rule that fired.</param>
/// <param name="ShiftKey">The shift, crew or pattern it applied to.</param>
/// <param name="Day">The weekday the shift starts on.</param>
/// <param name="Before">
/// The value before the rule: a working duration, a rest gap — or, for the
/// obligations (<see cref="WorkingTimeRuleId.ReplacementRestDay"/>), what the
/// pattern already provides, which for a repeating week is nothing.
/// </param>
/// <param name="After">
/// The value after the rule was applied — or, for the obligations, what is owed:
/// the compensating rest, the window the replacement day has to fall in.
/// </param>
public sealed record RuleApplication(WorkingTimeRuleId Rule, string ShiftKey, DayOfWeek Day, TimeSpan Before, TimeSpan After)
{
    /// <summary>The section this rule is written in.</summary>
    public string LegalReference =>
        WorkingTimeRules.Catalog.First(r => r.Id == Rule).LegalReference;
}

/// <summary>A stretch of actual work inside one shift instance, in seconds from Monday 00:00.</summary>
/// <param name="StartSeconds">Inclusive start.</param>
/// <param name="EndSeconds">Exclusive end.</param>
public sealed record WorkSpan(long StartSeconds, long EndSeconds)
{
    /// <summary>Length in seconds.</summary>
    public long DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>
/// One crew's shift in an ordinary week, after every cut, with the breaks taken
/// out. This is the person's view of the plan; <see cref="WorkingTimeline.WeekWindows"/>
/// is the machine's, and the two differ wherever two crews hand over.
/// </summary>
/// <param name="Crew">The crew that works it.</param>
/// <param name="ShiftKey">The shift definition it came from.</param>
/// <param name="Day">The weekday the crew reported for, midnight crossings included.</param>
/// <param name="StartSeconds">Start, in seconds from Monday 00:00; may run past the week for a shift that wraps.</param>
/// <param name="EndSeconds">End of the presence, breaks included.</param>
/// <param name="WorkingSpans">The stretches actually worked — the presence minus the §4 breaks.</param>
/// <param name="NightWork">Whether §2 (4) makes this night work, and §6 (2) therefore caps it.</param>
public sealed record CrewShiftInstance(
    string Crew,
    string ShiftKey,
    DayOfWeek Day,
    long StartSeconds,
    long EndSeconds,
    IReadOnlyList<WorkSpan> WorkingSpans,
    bool NightWork)
{
    /// <summary>Seconds of working time, breaks excluded.</summary>
    public long WorkingSeconds
    {
        get
        {
            long total = 0;
            foreach (var span in WorkingSpans)
                total += span.DurationSeconds;
            return total;
        }
    }

    /// <inheritdoc />
    public bool Equals(CrewShiftInstance? other) =>
        other is not null
        && string.Equals(Crew, other.Crew, StringComparison.Ordinal)
        && string.Equals(ShiftKey, other.ShiftKey, StringComparison.Ordinal)
        && Day == other.Day && StartSeconds == other.StartSeconds && EndSeconds == other.EndSeconds
        && NightWork == other.NightWork && WorkingSpans.SequenceEqual(other.WorkingSpans);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Crew, StringComparer.Ordinal);
        hash.Add(ShiftKey, StringComparer.Ordinal);
        hash.Add(Day);
        hash.Add(StartSeconds);
        hash.Add(EndSeconds);
        hash.Add(NightWork);
        foreach (var span in WorkingSpans)
            hash.Add(span);
        return hash.ToHashCode();
    }
}

/// <summary>
/// The engine's view of a timeline: a repeating weekly calendar with a phase,
/// plus the finite list of exceptions — ready to drop onto a <see cref="MachineCapacity"/>.
/// </summary>
/// <param name="Windows">Open windows within one week, sorted, relative to Monday 00:00.</param>
/// <param name="PeriodSeconds">One week — of wall-clock seconds, which is 604 800 even in the two weeks the clocks change.</param>
/// <param name="PhaseSeconds">Where in the week the horizon falls.</param>
/// <param name="Blackouts">Holidays and absences, relative to the horizon, sorted and merged.</param>
/// <param name="MaxBridgeableGapSeconds">The longest closed gap an operation may pause across — the longest §4 break.</param>
/// <param name="IsClosed">
/// True when the pattern has no working time left at all. The engine has no way
/// to say "never open" — an empty window list means "always open" there — so a
/// closed centre is handed a single one-second window, which rejects every real
/// operation. This flag is what keeps <see cref="LongestPlacementSeconds"/>
/// honest about it.
/// </param>
public sealed record MachineCalendar(
    IReadOnlyList<CapacityWindow> Windows,
    long PeriodSeconds,
    long PhaseSeconds,
    IReadOnlyList<CapacityBlackout> Blackouts,
    long MaxBridgeableGapSeconds,
    bool IsClosed = false)
{
    private long? _longestPlacement;

    /// <summary>
    /// The longest operation that can ever be placed on this calendar, in seconds;
    /// 0 for a closed centre, <c>long.MaxValue</c> for an unconstrained one.
    /// </summary>
    /// <remarks>
    /// Memoised: the engine's own computation walks the windows twice around the
    /// period and allocates a <see cref="MachineCapacity"/> to do it, and callers
    /// read this as if it were a field.
    /// </remarks>
    public long LongestPlacementSeconds =>
        _longestPlacement ??= IsClosed ? 0 : ApplyTo(new MachineCapacity(0, "")).LongestPlacementSeconds;

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

    /// <inheritdoc />
    public bool Equals(MachineCalendar? other) =>
        other is not null && PeriodSeconds == other.PeriodSeconds && PhaseSeconds == other.PhaseSeconds
        && MaxBridgeableGapSeconds == other.MaxBridgeableGapSeconds && IsClosed == other.IsClosed
        && Windows.SequenceEqual(other.Windows) && Blackouts.SequenceEqual(other.Blackouts);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PeriodSeconds);
        hash.Add(PhaseSeconds);
        hash.Add(MaxBridgeableGapSeconds);
        hash.Add(IsClosed);
        foreach (var window in Windows)
            hash.Add(window);
        foreach (var blackout in Blackouts)
            hash.Add(blackout);
        return hash.ToHashCode();
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

    private long? _weeklyWorkingSeconds;

    internal WorkingTimeline(
        ShiftPattern pattern,
        WorkingTimeRules rules,
        DateTime from,
        DateTime to,
        WeekCapacity capacity,
        IReadOnlyList<WeekWindow> weekGaps,
        IReadOnlyList<CrewShiftInstance> crewShifts,
        IReadOnlyList<TimelineSegment> exceptions,
        IReadOnlyList<RuleApplication> applications,
        IReadOnlyList<PublicHoliday> holidays)
    {
        Pattern = pattern;
        Rules = rules;
        From = from;
        To = to;
        Capacity = capacity;
        WeekGaps = weekGaps;
        CrewShifts = crewShifts;
        Exceptions = exceptions;
        Applications = applications;
        Holidays = holidays;
        Crews = crewShifts.Select(i => i.Crew).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    /// <summary>The pattern this was built from.</summary>
    public ShiftPattern Pattern { get; }

    /// <summary>The rules this was built with.</summary>
    public WorkingTimeRules Rules { get; }

    /// <summary>Start of the covered range, plant-local wall clock.</summary>
    public DateTime From { get; }

    /// <summary>End of the covered range (exclusive). Exceptions are materialised up to here.</summary>
    public DateTime To { get; }

    /// <summary>
    /// What the week is: unconstrained, staffed, or closed. Read this rather than
    /// the length of <see cref="WeekWindows"/> — an empty list is two different
    /// situations, and only this says which.
    /// </summary>
    public WeekCapacity Capacity { get; }

    /// <summary>
    /// Open windows within one week, relative to Monday 00:00, as the machine sees
    /// them: two crews handing over are one window. Empty both for a continuous
    /// pattern and for one the rules closed — see <see cref="Capacity"/>.
    /// </summary>
    public IReadOnlyList<WeekWindow> WeekWindows => Capacity.Windows;

    /// <summary>Closed stretches within one week and why, relative to Monday 00:00.</summary>
    public IReadOnlyList<WeekWindow> WeekGaps { get; }

    /// <summary>What each crew works in an ordinary week, after every cut.</summary>
    public IReadOnlyList<CrewShiftInstance> CrewShifts { get; }

    /// <summary>Holidays and absences inside the range, sorted and non-overlapping.</summary>
    public IReadOnlyList<TimelineSegment> Exceptions { get; }

    /// <summary>Every place a rule cut, delayed or split a shift, and every obligation the plan takes on.</summary>
    public IReadOnlyList<RuleApplication> Applications { get; }

    /// <summary>The holidays that closed the plant inside the range.</summary>
    public IReadOnlyList<PublicHoliday> Holidays { get; }

    /// <summary>The crews the pattern staffs, in a stable order.</summary>
    public IReadOnlyList<string> Crews { get; }

    /// <summary>
    /// How many Sundays the crew with the fewest keeps free in the year
    /// <see cref="From"/> falls in. See <see cref="FreeSundays"/> — the count is
    /// of the Sundays that year actually has, 52 or 53.
    /// </summary>
    public int FreeSundaysPerYear => FreeSundays(From.Year);

    /// <summary>
    /// How many of <paramref name="year"/>'s Sundays the pattern leaves free for
    /// the crew with the fewest.
    /// <para>
    /// A weekly pattern on its own can only say <i>whether</i> a crew works
    /// Sundays; a crew that does works every one of them, which is why
    /// <see cref="WorkingTimeRules.SundayRotationWeeks"/> exists. With a rota of
    /// four, a crew takes one Sunday in four and keeps the rest.
    /// </para>
    /// </summary>
    public int FreeSundays(int year)
    {
        // An unattended machine runs every Sunday and nobody's rest is at stake;
        // reporting 0 lets a UI say "runs every Sunday" while SundayRest says the
        // section does not apply.
        if (Capacity.IsUnconstrained)
            return 0;

        int sundays = SundaysIn(year);
        if (!WorksAnySunday)
            return sundays;

        int worked = (int)Math.Ceiling(sundays / (double)Rules.SundayRotationWeeks);
        return Math.Max(0, sundays - worked);
    }

    /// <summary>True when some crew is at the machine during the §9 Sunday rest window.</summary>
    public bool WorksAnySunday
    {
        get
        {
            long shift = (long)Rules.SundayBoundaryShift.TotalSeconds;
            foreach (var instance in CrewShifts)
                foreach (var span in instance.WorkingSpans)
                    if (span.StartSeconds < 7 * 24 * 3600 + shift && span.EndSeconds > 6 * 24 * 3600 + shift)
                        return true;
            return false;
        }
    }

    /// <summary>Where the pattern stands on §11 (1).</summary>
    public SundayRestStatus SundayRest
    {
        get
        {
            if (Capacity.IsUnconstrained)
                return SundayRestStatus.NotApplicable;
            if (!WorksAnySunday)
                return SundayRestStatus.Intact;
            return FreeSundaysPerYear >= Rules.MinimumFreeSundaysPerYear
                ? SundayRestStatus.Rotated
                : SundayRestStatus.Breach;
        }
    }

    /// <summary>True when the pattern breaks §11 (1): too few Sundays stay free.</summary>
    public bool ViolatesFreeSundays => SundayRest == SundayRestStatus.Breach;

    /// <summary>
    /// Seconds of working time in an ordinary week, as the machine sees it: the
    /// full week for an unconstrained pattern, zero for a closed one.
    /// </summary>
    public long WeeklyWorkingSeconds
    {
        get
        {
            if (_weeklyWorkingSeconds is { } cached)
                return cached;

            long total = 0;
            if (Capacity.IsUnconstrained)
            {
                total = WeekSeconds;
            }
            else
            {
                foreach (var window in WeekWindows)
                    total += window.EndSeconds - window.StartSeconds;
            }

            _weeklyWorkingSeconds = total;
            return total;
        }
    }

    /// <summary>Seconds one crew works in an ordinary week.</summary>
    public long WeeklyWorkingSecondsFor(string crew)
    {
        long total = 0;
        foreach (var instance in CrewShifts)
            if (string.Equals(instance.Crew, crew, StringComparison.Ordinal))
                total += instance.WorkingSeconds;
        return total;
    }

    /// <summary>
    /// The §3 sentence 2 and §6 (2) averaging duties, evaluated over the range
    /// this timeline covers on the plant's own clock. See
    /// <see cref="Evaluate(TimeZoneInfo?)"/> for the zone-aware form.
    /// </summary>
    public WorkingTimeCompliance Compliance => field ??= Evaluate(null);

    /// <summary>
    /// Works out what the plan actually costs a crew: hours per crew and calendar
    /// day, the rolling werktäglich average against §3 sentence 2 / §6 (2), and
    /// the days the caps are exceeded.
    /// <para>
    /// Pass a <paramref name="zone"/> to measure real elapsed hours rather than
    /// clock positions. On the two days a year the clocks change they differ, and
    /// the difference is not academic: a 22:00–06:00 night across the autumn
    /// change is nine hours of work, and §6 (2) counts nine.
    /// </para>
    /// </summary>
    /// <param name="zone">The plant's time zone, or null to stay on the clock.</param>
    public WorkingTimeCompliance Evaluate(TimeZoneInfo? zone) => WorkingTimeEvaluator.Evaluate(this, zone);

    /// <summary>
    /// The scheduler's view, with second 0 at <paramref name="horizon"/>. Exceptions
    /// before the horizon are dropped, those after <see cref="To"/> do not exist.
    /// </summary>
    public MachineCalendar ToMachineCalendar(DateTime horizon)
    {
        horizon = PlantTime.Wall(horizon);
        long phase = WeekOffsetSeconds(horizon);

        // A closed centre needs a calendar that is constrained but never open.
        // The engine reads an empty window list as "no constraint", so the only
        // way to say "never" is a window too short to hold anything.
        var windows = Capacity is WeekCapacity.Closed
            ? [new CapacityWindow(0, 1)]
            : WeekWindows.Select(w => new CapacityWindow(w.StartSeconds, w.EndSeconds)).ToList();

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
        // periods and closed days are not. The longest break is the threshold —
        // except on a closed centre, where bridging nothing to nothing would only
        // widen the sentinel window.
        bool closed = Capacity is WeekCapacity.Closed;
        long bridge = closed ? 0 : (long)Math.Max(Rules.BreakAfterNineHours.TotalSeconds, Rules.BreakAfterSixHours.TotalSeconds);
        return new MachineCalendar(windows, WeekSeconds, windows.Count == 0 ? 0 : phase, blackouts, bridge, closed);
    }

    /// <summary>
    /// The annotated timeline between two moments: the weekly pattern
    /// materialised across the dates, with holidays and absences overlaid.
    /// Contiguous, sorted, no gaps.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The requested range reaches outside <c>[From, To]</c>. Holidays and
    /// absences only exist inside it, so answering anyway would render Neujahr as
    /// an ordinary working day — contiguous, sorted and wrong.
    /// </exception>
    public IReadOnlyList<TimelineSegment> Segments(DateTime from, DateTime to)
    {
        from = PlantTime.Wall(from);
        to = PlantTime.Wall(to);
        if (to <= from)
            return [];
        if (from < From || to > To)
            throw new ArgumentOutOfRangeException(
                nameof(to), $"This timeline covers [{From:yyyy-MM-dd HH:mm}, {To:yyyy-MM-dd HH:mm}); [{from:yyyy-MM-dd HH:mm}, {to:yyyy-MM-dd HH:mm}) reaches outside it.");

        var raw = new List<TimelineSegment>();

        if (Capacity.IsUnconstrained)
        {
            raw.Add(new TimelineSegment(from, to, SegmentKind.Working, ""));
        }
        else
        {
            // Walk week by week from the Monday on or before `from`.
            var weekStart = from.Date.AddDays(-WeekdayIndex(from.DayOfWeek));
            var pieces = WeekWindows.Select(w => (Window: w, Kind: SegmentKind.Working))
                .Concat(WeekGaps.Select(g => (Window: g, Kind: g.Kind)))
                .OrderBy(p => p.Window.StartSeconds)
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

        // Overlay exceptions: whatever they cover is replaced. Both lists are
        // sorted and disjoint, so one moving index walks them together instead of
        // rescanning every exception for every segment.
        var result = new List<TimelineSegment>();
        int first = 0;
        foreach (var segment in raw)
        {
            while (first < Exceptions.Count && Exceptions[first].End <= segment.Start)
                first++;

            var cursor = segment.Start;
            for (int i = first; i < Exceptions.Count; i++)
            {
                var exception = Exceptions[i];
                if (exception.Start >= segment.End)
                    break;
                if (exception.End <= cursor)
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

    /// <summary>How many Sundays a calendar year holds — 52, or 53 about once in seven.</summary>
    internal static int SundaysIn(int year)
    {
        var first = new DateOnly(year, 1, 1);
        int offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;
        int days = DateTime.IsLeapYear(year) ? 366 : 365;
        return (days - offset + 6) / 7;
    }

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
