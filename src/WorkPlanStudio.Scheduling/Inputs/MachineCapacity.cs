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

    /// <summary>Setup matrix entries. A transition that is not listed costs nothing.</summary>
    public IReadOnlyList<SetupDuration> SetupDurations { get; init; } = [];

    /// <summary>The longest single window, or <c>long.MaxValue</c> when unconstrained.</summary>
    internal long LongestWindowSeconds => AvailabilityWindows.Count == 0
        ? long.MaxValue
        : AvailabilityWindows.Max(w => w.DurationSeconds);

    /// <summary>The worst change-over cost into <paramref name="family"/>, used for feasibility checks.</summary>
    internal long WorstSetupInto(string family) => SetupDurations.Count == 0
        ? 0
        : SetupDurations
            .Where(s => string.Equals(s.ToFamily, family, StringComparison.Ordinal))
            .Select(s => s.DurationSeconds)
            .DefaultIfEmpty(0)
            .Max();
}
