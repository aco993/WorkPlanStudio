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
    public long LongestPlacementSeconds
    {
        get
        {
            var windows = AvailabilityWindows;
            if (windows.Count == 0)
                return long.MaxValue;

            // Walk the windows twice around the period so a run that wraps the
            // boundary is measured once as a whole.
            long best = 0;
            for (int start = 0; start < windows.Count; start++)
            {
                long run = windows[start].DurationSeconds;
                long previousEnd = windows[start].EndSeconds;
                for (int step = 1; step < windows.Count * 2; step++)
                {
                    int index = (start + step) % windows.Count;
                    long offset = (start + step) / windows.Count * CalendarPeriodSeconds;
                    long nextStart = windows[index].StartSeconds + offset;
                    if (nextStart - previousEnd > MaxBridgeableGapSeconds)
                        break;
                    run += windows[index].DurationSeconds;
                    previousEnd = windows[index].EndSeconds + offset;
                }

                best = Math.Max(best, run);
            }

            return best;
        }
    }

    /// <summary>The worst change-over cost into <paramref name="family"/>, used for feasibility checks.</summary>
    internal long WorstSetupInto(string family) => SetupDurations.Count == 0
        ? 0
        : SetupDurations
            .Where(s => string.Equals(s.ToFamily, family, StringComparison.Ordinal))
            .Select(s => s.DurationSeconds)
            .DefaultIfEmpty(0)
            .Max();
}
