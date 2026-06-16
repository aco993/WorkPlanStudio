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
    Schedule Run(
        SchedulingContext context,
        IReadOnlyList<int> jobPriorityOrder,
        IReadOnlyDictionary<int, long> dueByJob);
}
