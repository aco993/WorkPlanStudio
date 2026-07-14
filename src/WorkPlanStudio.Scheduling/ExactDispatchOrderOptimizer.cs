namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Exhaustively evaluates every job-priority permutation for small instances.
/// The returned proof is exact within <see cref="DispatchScheduler"/>'s dispatch-order
/// model; it is deliberately not described as a general job-shop optimality proof.
/// </summary>
public static class ExactDispatchOrderOptimizer
{
    /// <summary>9! = 362,880 schedules, the hard guard against accidental combinatorial explosions.</summary>
    public const int MaxJobs = 9;

    /// <summary>Evaluates every job-priority permutation and returns the best dispatch schedule with proof metadata.</summary>
    public static ExactDispatchOrderResult Run(
        SchedulingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Jobs.Count > MaxJobs)
            throw new ArgumentOutOfRangeException(nameof(context), $"Exact dispatch-order optimization supports at most {MaxJobs} jobs.");
        cancellationToken.ThrowIfCancellationRequested();

        var dueByJob = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();
        if (context.Jobs.Count == 0)
        {
            var empty = scheduler.RunCancellable(context, [], dueByJob, cancellationToken);
            return new ExactDispatchOrderResult(
                new SchedulingResult(empty, ScheduleEvaluator.Evaluate(empty, context), dueByJob, 0),
                1,
                true);
        }

        var order = Enumerable.Range(0, context.Jobs.Count).ToArray();
        Schedule? bestSchedule = null;
        ScheduleEvaluation? bestEvaluation = null;
        long evaluatedOrders = 0;

        EvaluatePermutations(0);
        return new ExactDispatchOrderResult(
            new SchedulingResult(bestSchedule!, bestEvaluation!, dueByJob, 0),
            evaluatedOrders,
            true);

        void EvaluatePermutations(int start)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (start == order.Length)
            {
                var schedule = scheduler.RunCancellable(context, order, dueByJob, cancellationToken);
                var evaluation = ScheduleEvaluator.Evaluate(schedule, context);
                evaluatedOrders++;
                if (bestEvaluation is null || evaluation.Penalty < bestEvaluation.Penalty)
                {
                    bestSchedule = schedule;
                    bestEvaluation = evaluation;
                }
                return;
            }

            for (var candidate = start; candidate < order.Length; candidate++)
            {
                (order[start], order[candidate]) = (order[candidate], order[start]);
                EvaluatePermutations(start + 1);
                (order[start], order[candidate]) = (order[candidate], order[start]);
            }
        }
    }
}

/// <summary>
/// An exact result within the finite dispatch-order search space.
/// </summary>
/// <param name="Result">The best schedule and evaluation found.</param>
/// <param name="EvaluatedOrders">Number of complete job orders evaluated.</param>
/// <param name="IsOptimalWithinDispatchOrderModel">Always true for a completed exhaustive run.</param>
public sealed record ExactDispatchOrderResult(
    SchedulingResult Result,
    long EvaluatedOrders,
    bool IsOptimalWithinDispatchOrderModel);
