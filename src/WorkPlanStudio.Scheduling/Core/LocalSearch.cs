namespace WorkPlanStudio.Scheduling;

/// <summary>The outcome of a local-search descent.</summary>
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
/// Steepest-descent hill climb over the job priority order using the
/// <b>insertion</b> (or-opt) neighbourhood: take one job out of the sequence and
/// re-insert it at every other position. Each pass evaluates all n·(n−1)
/// neighbours and adopts the single best strict improvement, so the incumbent is
/// never replaced by something worse and the result is guaranteed no worse than
/// the starting order. Because the search perturbs the priority order and
/// re-dispatches, every candidate it considers is feasible by construction.
/// <para>
/// The neighbourhood matters far more than the acceptance strategy. Adjacent
/// swaps — the obvious first choice, and what this used to do — move a job only
/// one position per improving step, so a job that belongs ten places earlier is
/// unreachable unless every position on the way there also improves; on a
/// tardiness objective it usually does not, and the descent stalls after a
/// handful of its budget. Insertion reaches that position in one move. Measured
/// against exhaustive enumeration in <c>OptimalityTests</c>: adjacent swaps leave
/// a 27 % mean penalty gap to the optimum, insertion leaves 0.2 %. See ADR 0008.
/// </para>
/// </summary>
public static class LocalSearch
{
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Improves <paramref name="startOrder"/>, returning the best order found —
    /// never worse than the start — with its schedule and score.
    /// <paramref name="maxSteps"/> caps neighbour evaluations; 0 disables the search.
    /// </summary>
    public static LocalSearchResult Improve(
        IScheduler scheduler,
        SchedulingContext context,
        IReadOnlyDictionary<int, long> dueByJob,
        IReadOnlyList<int> startOrder,
        Schedule startSchedule,
        ScheduleEvaluation startEvaluation,
        int maxSteps) =>
        ImproveCancellable(scheduler, context, dueByJob, startOrder, startSchedule, startEvaluation, maxSteps, CancellationToken.None);

    /// <summary>Improves a priority order and observes cooperative cancellation.</summary>
    public static LocalSearchResult ImproveCancellable(
        IScheduler scheduler,
        SchedulingContext context,
        IReadOnlyDictionary<int, long> dueByJob,
        IReadOnlyList<int> startOrder,
        Schedule startSchedule,
        ScheduleEvaluation startEvaluation,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        var bestOrder = startOrder.ToArray();
        var bestSchedule = startSchedule;
        var bestEvaluation = startEvaluation;

        int n = bestOrder.Length;
        int steps = 0;
        bool improved = true;
        var candidate = new int[n];

        while (improved && steps < maxSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            improved = false;

            int[]? passOrder = null;
            Schedule? passSchedule = null;
            ScheduleEvaluation? passEvaluation = null;

            for (int from = 0; from < n && steps < maxSteps; from++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int to = 0; to < n && steps < maxSteps; to++)
                {
                    if (from == to)
                        continue;

                    Reinsert(bestOrder, candidate, from, to);
                    steps++;

                    var schedule = scheduler.RunCancellable(context, candidate, dueByJob, cancellationToken);
                    var evaluation = ScheduleEvaluator.Evaluate(schedule, context);

                    double incumbent = passEvaluation?.Penalty ?? bestEvaluation.Penalty;
                    if (evaluation.Penalty < incumbent - Epsilon)
                    {
                        passOrder = candidate[..];
                        passSchedule = schedule;
                        passEvaluation = evaluation;
                    }
                }
            }

            if (passOrder is not null)
            {
                bestOrder = passOrder;
                bestSchedule = passSchedule!;
                bestEvaluation = passEvaluation!;
                improved = true;
            }
        }

        return new LocalSearchResult(bestOrder, bestSchedule, bestEvaluation, steps);
    }

    /// <summary>Copies <c>source</c> into <c>target</c> with the element at <c>from</c> moved to index <c>to</c>.</summary>
    private static void Reinsert(int[] source, int[] target, int from, int to)
    {
        int value = source[from];
        int w = 0;
        for (int r = 0; r <= source.Length; r++)
        {
            if (w == to)
                target[w++] = value;
            if (r < source.Length && r != from)
                target[w++] = source[r];
        }
    }
}
