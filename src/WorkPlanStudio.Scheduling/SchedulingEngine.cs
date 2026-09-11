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

        var dueByJob = DueDateAssigner.Assign(context);
        var outcome = Search(context, dueByJob, cancellationToken);

        return new SchedulingResult(outcome.Schedule, outcome.Evaluation, dueByJob, outcome.StepsUsed)
        {
            EquivalentRules = PriorityOrdering.EquivalentRules(context, dueByJob)
        };
    }

    /// <summary>The search without the equivalent-rule roll-up, for callers that only need the score.</summary>
    internal SearchOutcome Search(
        SchedulingContext context, IReadOnlyDictionary<int, long> dueByJob, CancellationToken cancellationToken)
    {
        var evaluator = LocalSearch.EvaluatorFor(_scheduler, dueByJob);
        var workspace = SchedulingWorkspace.For(context, dueByJob);

        if (context.Jobs.Count == 0)
        {
            var emptySchedule = evaluator.Materialise(context, [], workspace);
            return new SearchOutcome(emptySchedule, ScheduleEvaluator.Evaluate(emptySchedule, context), 0);
        }

        var baseOrder = PriorityOrdering.For(context, dueByJob);
        int restarts = context.Parameters.MultiStartRuns;
        int budget = context.Parameters.LocalSearchMaxSteps;
        var acceptance = context.Parameters.LocalSearchAcceptance;

        var order = new int[baseOrder.Length];
        var bestOrder = new int[baseOrder.Length];
        double bestPenalty = double.PositiveInfinity;
        int totalSteps = 0;

        // A descent from every restart, not just from the best raw shuffle: the
        // starting point of a descent matters much less than how far it can walk,
        // and polishing only the best shuffle wastes the other restarts entirely.
        for (int restart = 0; restart < restarts; restart++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Restart 0 is the pure rule order, so the chosen schedule can never be
            // worse than what the dispatch rule alone produces.
            Array.Copy(baseOrder, order, order.Length);
            if (restart > 0)
                DeterministicRandom.ForRun(context.Parameters.Seed, restart).Shuffle(order);

            var start = evaluator.Score(context, order, workspace, cancellationToken);
            var descent = LocalSearch.Descend(
                evaluator, context, workspace, order, start, budget, acceptance, cancellationToken);
            totalSteps += descent.StepsUsed;

            // Strict improvement, so restart 0 keeps ties and the result does not
            // depend on how many restarts were configured.
            if (descent.Penalty < bestPenalty)
            {
                bestPenalty = descent.Penalty;
                Array.Copy(order, bestOrder, order.Length);
            }
        }

        // One materialised schedule per run, for the order that won.
        evaluator.Score(context, bestOrder, workspace, cancellationToken);
        var schedule = evaluator.Materialise(context, bestOrder, workspace);
        return new SearchOutcome(schedule, ScheduleEvaluator.Evaluate(schedule, context), totalSteps);
    }

    /// <summary>What one multi-start search produced.</summary>
    /// <param name="Schedule">The winning schedule.</param>
    /// <param name="Evaluation">Its KPIs and penalty.</param>
    /// <param name="StepsUsed">Neighbours evaluated across every restart.</param>
    internal readonly record struct SearchOutcome(Schedule Schedule, ScheduleEvaluation Evaluation, int StepsUsed);
}
