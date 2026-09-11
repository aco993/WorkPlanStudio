namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Orchestrates a full scheduling run as a GRASP-style multi-start:
/// <list type="number">
/// <item>assign each job a target date (<see cref="DueDateAssigner"/>);</item>
/// <item>build the rule-based priority order (<see cref="PriorityOrdering"/>);</item>
/// <item>run a <see cref="LocalSearch"/> descent from the rule order and from each
/// seeded shuffle of it, keeping the best result.</item>
/// </list>
/// Restart 0 is always the rule order and the descent never regresses, so the
/// result can never be worse than the pure rule schedule, and it is fully
/// reproducible for a given seed.
/// <para>
/// Every restart shares one <see cref="SchedulingWorkspace"/> and only the
/// winning order is turned into a <see cref="Schedule"/>, so a run allocates a
/// fixed amount regardless of how many candidates it evaluates.
/// </para>
/// </summary>
public sealed class SchedulingEngine
{
    private readonly IScheduler _scheduler;

    /// <summary>Creates an engine using <paramref name="scheduler"/> (defaults to <see cref="DispatchScheduler"/>).</summary>
    public SchedulingEngine(IScheduler? scheduler = null) =>
        _scheduler = scheduler ?? new DispatchScheduler();

    /// <summary>Runs the full pipeline (due dates → multi-start descents) and returns the best schedule.</summary>
    public SchedulingResult Run(SchedulingContext context) => RunCancellable(context, CancellationToken.None);

    /// <summary>Runs the full pipeline and observes cooperative cancellation.</summary>
    public SchedulingResult RunCancellable(SchedulingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Begin(context).Complete(cancellationToken);
    }

    /// <summary>
    /// Prepares the same run but hands the restart boundary back to the caller —
    /// see <see cref="MultiStartRun"/>, which is what a single-threaded host needs
    /// in order to stay responsive, report progress and honour cancellation.
    /// </summary>
    /// <remarks>
    /// A descent from every restart, not just from the best raw shuffle: the
    /// starting point of a descent matters much less than how far it can walk, and
    /// polishing only the best shuffle wastes the other restarts entirely. Target
    /// dates are assigned here, once, before any restart runs.
    /// </remarks>
    public MultiStartRun Begin(SchedulingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new MultiStartRun(_scheduler, context, DueDateAssigner.Assign(context));
    }

    /// <summary>The search without the equivalent-rule roll-up, for callers that only need the score.</summary>
    internal SearchOutcome Search(
        SchedulingContext context, IReadOnlyDictionary<int, long> dueByJob, CancellationToken cancellationToken) =>
        new MultiStartRun(_scheduler, context, dueByJob).CompleteOutcome(cancellationToken);

    /// <summary>What one multi-start search produced.</summary>
    /// <param name="Schedule">The winning schedule.</param>
    /// <param name="Evaluation">Its KPIs and penalty.</param>
    /// <param name="StepsUsed">Neighbours evaluated across every restart.</param>
    internal readonly record struct SearchOutcome(Schedule Schedule, ScheduleEvaluation Evaluation, int StepsUsed);
}
