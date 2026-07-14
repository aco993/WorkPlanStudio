namespace WorkPlanStudio.Scheduling;

/// <summary>
/// The capacity of a work center: how many operations it can run at the same
/// time (<paramref name="ParallelCapacity"/> identical slots). This is the only
/// <b>hard</b> capacity constraint in the model — each slot is strictly serial.
/// Optional availability windows and sequence-dependent setup transitions add
/// calendar feasibility without introducing wall-clock or time-zone concerns
/// into the deterministic core.
/// </summary>
/// <param name="WorkCenterId">Identifier matching <see cref="JobStep.WorkCenterId"/>.</param>
/// <param name="Name">Display name (e.g. "CNC-300 — 5-Axis Milling Center").</param>
/// <param name="ParallelCapacity">Number of parallel slots (≥ 1). Defaults to 1.</param>
public sealed record MachineCapacity(int WorkCenterId, string Name, int ParallelCapacity = 1)
{
    /// <summary>Sorted usable windows. Empty means continuously available.</summary>
    public IReadOnlyList<CapacityWindow> AvailabilityWindows { get; init; } = [];

    /// <summary>Setup matrix entries. A missing transition has zero extra setup.</summary>
    public IReadOnlyList<SetupDuration> SetupDurations { get; init; } = [];
}
