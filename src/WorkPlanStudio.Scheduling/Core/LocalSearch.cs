namespace WorkPlanStudio.Scheduling;

/// <summary>The outcome of a local-search polish.</summary>
/// <param name="Order">The (possibly improved) priority order.</param>
/// <param name="Schedule">The schedule for <paramref name="Order"/>.</param>
/// <param name="Evaluation">Its score.</param>
/// <param name="StepsUsed">How many neighbours were evaluated.</param>
public sealed record LocalSearchResult(
    IReadOnlyList<int> Order,
    Schedule Schedule,
    ScheduleEvaluation Evaluation,
    int StepsUsed);

/// <summary>
/// A first-improvement hill-climb over the job priority order. Neighbours are
/// adjacent swaps, enumerated in a fixed order; a neighbour is adopted only if it
/// <b>strictly</b> improves the penalty, and the incumbent is never replaced by a
/// worse schedule — so the result is guaranteed never worse than the starting
/// point. Because the search perturbs the priority order and re-dispatches, every
/// candidate it considers is feasible by construction.
/// </summary>
public static class LocalSearch
{
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Polishes <paramref name="startOrder"/> by adjacent swaps, returning the best
    /// order found — guaranteed never worse than the start — with its schedule and score.
    /// </summary>
    /// <param name="scheduler">Scheduler used to re-dispatch each candidate order.</param>
    /// <param name="context">The scheduling context.</param>
    /// <param name="dueByJob">Assigned target dates.</param>
    /// <param name="startOrder">The incumbent priority order to improve.</param>
    /// <param name="startSchedule">The incumbent schedule.</param>
    /// <param name="startEvaluation">The incumbent score.</param>
    /// <param name="maxSteps">Maximum neighbour evaluations (0 disables the search).</param>
    public static LocalSearchResult Improve(
        IScheduler scheduler,
        SchedulingContext context,
        IReadOnlyDictionary<int, long> dueByJob,
        IReadOnlyList<int> startOrder,
        Schedule startSchedule,
        ScheduleEvaluation startEvaluation,
        int maxSteps)
    {
        var bestOrder = startOrder.ToArray();
        var bestSchedule = startSchedule;
        var bestEvaluation = startEvaluation;

        int steps = 0;
        bool improved = true;

        while (improved && steps < maxSteps)
        {
            improved = false;

            for (int i = 0; i < bestOrder.Length - 1; i++)
            {
                if (steps >= maxSteps) break;

                var candidate = (int[])bestOrder.Clone();
                (candidate[i], candidate[i + 1]) = (candidate[i + 1], candidate[i]);
                steps++;

                var schedule = scheduler.Run(context, candidate, dueByJob);
                var evaluation = ScheduleEvaluator.Evaluate(schedule, context);

                if (evaluation.Penalty < bestEvaluation.Penalty - Epsilon)
                {
                    bestOrder = candidate;
                    bestSchedule = schedule;
                    bestEvaluation = evaluation;
                    improved = true;
                    break; // first improvement → restart the scan from the new incumbent
                }
            }
        }

        return new LocalSearchResult(bestOrder, bestSchedule, bestEvaluation, steps);
    }
}
