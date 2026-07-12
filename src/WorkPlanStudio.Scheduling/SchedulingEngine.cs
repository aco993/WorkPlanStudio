namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Orchestrates a full scheduling run, mirroring the proven GRASP-style pattern:
/// <list type="number">
/// <item>assign each job a target date (<see cref="DueDateAssigner"/>);</item>
/// <item>build the rule-based priority order (<see cref="PriorityOrdering"/>);</item>
/// <item>multi-start — evaluate the rule order plus several seeded shuffles, keep the best;</item>
/// <item>local-search polish (<see cref="LocalSearch"/>), which can only improve on the best start.</item>
/// </list>
/// The result is therefore never worse than the pure rule schedule, and is fully
/// reproducible for a given seed.
/// </summary>
public sealed class SchedulingEngine
{
    private readonly IScheduler _scheduler;

    /// <summary>Creates an engine using <paramref name="scheduler"/> (defaults to <see cref="DispatchScheduler"/>).</summary>
    public SchedulingEngine(IScheduler? scheduler = null) =>
        _scheduler = scheduler ?? new DispatchScheduler();

    /// <summary>Runs the full pipeline (due dates → multi-start → local search) and returns the best schedule.</summary>
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

        // Run 0: the pure rule order — always a candidate, so the chosen schedule
        // can never be worse than what the dispatch rule alone produces.
        var baseOrder = PriorityOrdering.For(context, dueByJob);
        int[] bestOrder = baseOrder;
        var bestSchedule = _scheduler.RunCancellable(context, baseOrder, dueByJob, cancellationToken);
        var bestEvaluation = ScheduleEvaluator.Evaluate(bestSchedule, context);

        // Runs 1..N-1: seeded shuffles for diversity. Strict-improvement keeps the
        // tie with run 0, so equal-quality shuffles never displace the rule order.
        int runs = Math.Max(1, context.Parameters.MultiStartRuns);
        for (int i = 1; i < runs; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var order = (int[])baseOrder.Clone();
            DeterministicRandom.ForRun(context.Parameters.Seed, i).Shuffle(order);

            var schedule = _scheduler.RunCancellable(context, order, dueByJob, cancellationToken);
            var evaluation = ScheduleEvaluator.Evaluate(schedule, context);
            if (evaluation.Penalty < bestEvaluation.Penalty)
            {
                bestOrder = order;
                bestSchedule = schedule;
                bestEvaluation = evaluation;
            }
        }

        var polished = LocalSearch.ImproveCancellable(
            _scheduler, context, dueByJob,
            bestOrder, bestSchedule, bestEvaluation,
            context.Parameters.LocalSearchMaxSteps,
            cancellationToken);

        return new SchedulingResult(polished.Schedule, polished.Evaluation, dueByJob, polished.StepsUsed);
    }
}
