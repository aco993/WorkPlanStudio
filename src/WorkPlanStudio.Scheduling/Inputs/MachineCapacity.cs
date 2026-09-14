namespace WorkPlanStudio.Scheduling;

/// <summary>
/// A work center and the constraints it imposes: how many operations it can run
/// at once, when it is available, and what changing over between operation
/// families costs.
/// <para>
/// <paramref name="ParallelCapacity"/> is the hard concurrency limit — each of
/// those slots is strictly serial, so the work center never runs more than that
/// many operations at the same time.
/// </para>
/// <para>
/// <see cref="AvailabilityWindows"/> and <see cref="SetupDurations"/> are both
/// optional and default to "no constraint": a work center with no windows is
/// continuously available, and a missing setup transition is free. That keeps
/// the simple case simple — most callers never set either.
/// </para>
/// </summary>
/// <param name="WorkCenterId">Identifier matching <see cref="JobStep.WorkCenterId"/>.</param>
/// <param name="Name">Display name (e.g. "CNC-300 — 5-Axis Milling Center").</param>
/// <param name="ParallelCapacity">Number of parallel slots (1..64). Defaults to 1.</param>
public sealed record MachineCapacity(int WorkCenterId, string Name, int ParallelCapacity = 1)
{
    /// <summary>
    /// Usable windows <b>within one calendar period</b>, sorted and
    /// non-overlapping. Empty means continuously available, which is the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shop calendar repeats: "available 06:00–14:00 every day" is one window
    /// in a 24-hour period, not 365 separate windows. Modelling it as a finite
    /// list would either run out mid-schedule or force the caller to materialise
    /// a year of them, so the list describes one period and
    /// <see cref="CalendarPeriodSeconds"/> says how long that period is.
    /// </para>
    /// <para>
    /// An operation must fit entirely inside one window — the model has no
    /// preemption, so work cannot be suspended overnight and resumed. That is
    /// checked when the <see cref="SchedulingContext"/> is built rather than
    /// during dispatch, so the search can never trip over it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<CapacityWindow> AvailabilityWindows { get; init; } = [];

    /// <summary>
    /// Length of the repeating calendar period. Required when
    /// <see cref="AvailabilityWindows"/> is non-empty; every window must lie
    /// inside <c>[0, CalendarPeriodSeconds)</c>.
    /// </summary>
    public long CalendarPeriodSeconds { get; init; }

    /// <summary>
    /// Where in the repeating period the engine's second 0 falls, in
    /// <c>[0, CalendarPeriodSeconds)</c>. Defaults to 0.
    /// </summary>
    /// <remarks>
    /// A weekly pattern is naturally written from Monday 00:00, but the planning
    /// horizon rarely starts on a Monday at midnight. Rather than forcing every
    /// caller to rotate its windows, the calendar declares the phase and the
    /// dispatcher shifts the axis before looking for a fit. A Wednesday-morning
    /// horizon on a weekly calendar is <c>2 days + 8 hours</c>.
    /// </remarks>
    public long CalendarPhaseSeconds { get; init; }

    /// <summary>
    /// Absolute closed intervals layered on top of the repeating calendar, sorted
    /// and non-overlapping. Empty by default. See <see cref="CapacityBlackout"/>.
    /// </summary>
    public IReadOnlyList<CapacityBlackout> Blackouts { get; init; } = [];

    /// <summary>
    /// The longest gap between two windows that an operation may <i>pause</i>
    /// across and resume after, in seconds. 0 (the default) means an operation
    /// must fit inside one window.
    /// </summary>
    /// <remarks>
    /// A crew's 30-minute break does not scrap a half-finished part; the machine
    /// waits and the job resumes. The end of a shift, with no night crew, is a
    /// different matter — the part is finished tomorrow only if the process
    /// tolerates it, and this model says it does not. The threshold is what
    /// separates the two: gaps up to it are bridged with the operation's end
    /// pushed out by the pause, longer gaps end the search for a start in that
    /// window. Blackouts are never bridged.
    /// </remarks>
    public long MaxBridgeableGapSeconds { get; init; }

    /// <summary>Setup matrix entries. A transition that is not listed costs nothing.</summary>
    public IReadOnlyList<SetupDuration> SetupDurations { get; init; } = [];

    /// <summary>
    /// The most contiguous working time any operation can get: the longest window,
    /// or — with bridging — the longest run of windows joined by bridgeable gaps.
    /// <c>long.MaxValue</c> when unconstrained.
    /// </summary>
    /// <remarks>
    /// A run that closes the period is unbounded, not long: if every gap in the
    /// calendar (the wrap from the last window back to the first included) is
    /// bridgeable, the machine never stops for longer than an operation may pause,
    /// so an operation of any length fits. Measuring such a calendar as a finite
    /// number is how a machine open 95.8 % of the day came to reject a 55-hour
    /// operation that the dispatcher places without trouble.
    /// </remarks>
    public long LongestPlacementSeconds
    {
        get
        {
            var windows = AvailabilityWindows;
            int count = windows.Count;
            if (count == 0)
                return long.MaxValue;

            long best = 0;
            for (int start = 0; start < count; start++)
            {
                long run = windows[start].DurationSeconds;
                long previousEnd = windows[start].EndSeconds;

                // One lap is enough: arriving back at `start` means every gap on
                // the way — including the period wrap — was bridgeable.
                for (int step = 1; step <= count; step++)
                {
                    int index = (start + step) % count;
                    long offset = (start + step) / count * CalendarPeriodSeconds;
                    long nextStart = windows[index].StartSeconds + offset;
                    if (nextStart - previousEnd > MaxBridgeableGapSeconds)
                        break;
                    if (index == start)
                        return long.MaxValue;

                    run += windows[index].DurationSeconds;
                    previousEnd = windows[index].EndSeconds + offset;
                }

                best = Math.Max(best, run);
            }

            return best;
        }
    }

    /// <summary>
    /// Seconds this work center is open between second 0 and <paramref name="untilSeconds"/>:
    /// the calendar windows that fall inside the range, minus the blackouts that
    /// fall inside those windows. The whole range when unconstrained.
    /// </summary>
    /// <remarks>
    /// This is what utilisation should divide by. Dividing by the makespan
    /// instead reports a machine that ran flat out through every shift it had as
    /// 30 % busy, because the nights and the weekend count against it.
    /// </remarks>
    public long OpenSecondsWithin(long untilSeconds)
    {
        if (untilSeconds <= 0)
            return 0;

        long open = OpenSecondsBetween(0, untilSeconds);

        foreach (var blackout in Blackouts)
        {
            if (blackout.StartSeconds >= untilSeconds)
                break;
            open -= OpenSecondsBetween(blackout.StartSeconds, Math.Min(blackout.EndSeconds, untilSeconds));
        }

        return Math.Max(0, open);
    }

    /// <summary>Seconds of calendar windows inside <c>[from, to)</c>, ignoring blackouts.</summary>
    /// <remarks>
    /// Closed form rather than a walk over the periods in the range. The walk was
    /// <c>O(horizon / period)</c> and ran once per work center per blackout per
    /// scored schedule, so on a year of holidays and Sundays — exactly what the
    /// sibling working-time library produces — it dominated the evaluator.
    /// </remarks>
    private long OpenSecondsBetween(long from, long to)
    {
        if (to <= from)
            return 0;
        if (AvailabilityWindows.Count == 0)
            return to - from;

        return OpenSecondsBefore(to + CalendarPhaseSeconds) - OpenSecondsBefore(from + CalendarPhaseSeconds);
    }

    /// <summary>Open seconds in <c>[0, x)</c> of the repeating pattern, on the calendar's own axis.</summary>
    private long OpenSecondsBefore(long x)
    {
        if (x <= 0)
            return 0;

        long period = CalendarPeriodSeconds;
        long fullPeriods = x / period;
        long remainder = x - fullPeriods * period;

        long openPerPeriod = 0;
        long partial = 0;
        foreach (var window in AvailabilityWindows)
        {
            openPerPeriod += window.DurationSeconds;
            long end = Math.Min(window.EndSeconds, remainder);
            if (end > window.StartSeconds)
                partial += end - window.StartSeconds;
        }

        return fullPeriods * openPerPeriod + partial;
    }
}
