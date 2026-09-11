namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Turns a job priority order into a concrete, feasible schedule. Implementations
/// must be pure functions of their arguments (no hidden state) so the engine can
/// call them thousands of times during multi-start and local search and rely on
/// reproducible results.
/// </summary>
public interface IScheduler
{
    /// <summary>A short name for diagnostics / UI.</summary>
    string Name { get; }

    /// <summary>
    /// Builds a schedule for the given priority order.
    /// </summary>
    /// <param name="context">Jobs, machines and parameters.</param>
    /// <param name="jobPriorityOrder">Indices into <see cref="SchedulingContext.Jobs"/>, highest priority first.</param>
    /// <param name="dueByJob">Assigned target dates, used to populate the per-job outcome.</param>
    /// <param name="cancellationToken">Observed between jobs.</param>
    Schedule Run(
        SchedulingContext context,
        IReadOnlyList<int> jobPriorityOrder,
        IReadOnlyDictionary<int, long> dueByJob,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The evaluation-only half of a scheduler: scores an order into a reusable
/// <see cref="SchedulingWorkspace"/> without building a <see cref="Schedule"/>.
/// <para>
/// Local search and the exhaustive optimiser need the penalty, not the schedule —
/// they discard every candidate but the incumbent. Separating the two means a
/// candidate costs no allocation at all, and the schedule object is materialised
/// once, for the winner. A scheduler that does not implement this still works;
/// the engine falls back to building and scoring a full schedule per candidate,
/// which is what every candidate used to cost.
/// </para>
/// </summary>
public interface IOrderEvaluator
{
    /// <summary>
    /// Dispatches <paramref name="jobPriorityOrder"/> into <paramref name="workspace"/>
    /// and returns its score. Allocates nothing.
    /// </summary>
    /// <param name="context">Jobs, machines and parameters.</param>
    /// <param name="jobPriorityOrder">Indices into <see cref="SchedulingContext.Jobs"/>, highest priority first.</param>
    /// <param name="workspace">Scratch space sized for <paramref name="context"/>; overwritten.</param>
    /// <param name="cancellationToken">Observed between jobs.</param>
    ScheduleScore Score(
        SchedulingContext context,
        ReadOnlySpan<int> jobPriorityOrder,
        SchedulingWorkspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Projects whatever <see cref="Score"/> last placed in <paramref name="workspace"/>
    /// into an immutable <see cref="Schedule"/>.
    /// </summary>
    /// <param name="context">The context the workspace was filled for.</param>
    /// <param name="jobPriorityOrder">The order that was last scored.</param>
    /// <param name="workspace">The workspace holding that dispatch.</param>
    Schedule Materialise(
        SchedulingContext context,
        ReadOnlySpan<int> jobPriorityOrder,
        SchedulingWorkspace workspace);
}
