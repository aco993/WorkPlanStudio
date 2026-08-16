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

        if (context.Jobs.Count == 0)
        {
            var emptySchedule = _scheduler.RunCancellable(context, [], dueByJob, cancellationToken);
            return new SchedulingResult(emptySchedule, ScheduleEvaluator.Evaluate(emptySchedule, context), dueByJob, 0);
        }

        var baseOrder = PriorityOrdering.For(context, dueByJob);
        int restarts = Math.Max(1, context.Parameters.MultiStartRuns);
        int budget = context.Parameters.LocalSearchMaxSteps;

        Schedule? bestSchedule = null;
        ScheduleEvaluation? bestEvaluation = null;
        int totalSteps = 0;

        // A descent from every restart, not just from the best raw shuffle: the
        // starting point of a descent matters much less than how far it can walk,
        // and polishing only the best shuffle wastes the other restarts entirely.
        for (int restart = 0; restart < restarts; restart++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Restart 0 is the pure rule order, so the chosen schedule can never be
            // worse than what the dispatch rule alone produces.
            var order = (int[])baseOrder.Clone();
            if (restart > 0)
                DeterministicRandom.ForRun(context.Parameters.Seed, restart).Shuffle(order);

            var schedule = _scheduler.RunCancellable(context, order, dueByJob, cancellationToken);
            var evaluation = ScheduleEvaluator.Evaluate(schedule, context);

            var polished = LocalSearch.ImproveCancellable(
                _scheduler, context, dueByJob, order, schedule, evaluation, budget, cancellationToken);
            totalSteps += polished.StepsUsed;

            // Strict improvement, so restart 0 keeps ties and the result does not
            // depend on how many restarts were configured.
            if (bestEvaluation is null || polished.Evaluation.Penalty < bestEvaluation.Penalty)
            {
                bestSchedule = polished.Schedule;
                bestEvaluation = polished.Evaluation;
            }
        }

        return new SchedulingResult(bestSchedule!, bestEvaluation!, dueByJob, totalSteps);
    }
}
