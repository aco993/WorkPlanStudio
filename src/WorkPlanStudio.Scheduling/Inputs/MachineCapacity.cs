namespace WorkPlanStudio.Scheduling;

/// <summary>
/// The capacity of a work center: how many operations it can run at the same
/// time (<paramref name="ParallelCapacity"/> identical slots). This is the only
/// <b>hard</b> capacity constraint in the model — each slot is strictly serial,
/// so a work center can never run more than <paramref name="ParallelCapacity"/>
/// operations concurrently.
/// <para>
/// "Minutes per working day" (see <see cref="SchedulingParameters.MinutesPerWorkingDay"/>)
/// is deliberately <i>not</i> modelled here: it is a display concept used only to
/// bucket the abstract work-time axis into calendar days for the Gantt chart, not
/// a within-day cut-off. Keeping the core free of a working-day calendar is what
/// keeps the algorithm simple and provably feasible.
/// </para>
/// </summary>
/// <param name="WorkCenterId">Identifier matching <see cref="JobStep.WorkCenterId"/>.</param>
/// <param name="Name">Display name (e.g. "CNC-300 — 5-Axis Milling Center").</param>
/// <param name="ParallelCapacity">Number of parallel slots (≥ 1). Defaults to 1.</param>
public sealed record MachineCapacity(int WorkCenterId, string Name, int ParallelCapacity = 1);
