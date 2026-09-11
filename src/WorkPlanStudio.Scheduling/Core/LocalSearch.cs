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
/// Hill climb over the job priority order using the <b>insertion</b> (or-opt)
/// neighbourhood: take one job out of the sequence and re-insert it at every
/// other position. The incumbent is never replaced by something worse, so the
/// result is guaranteed no worse than the starting order, and because the search
/// perturbs the priority order and re-dispatches, every candidate it considers is
/// feasible by construction.
/// <para>
/// The neighbourhood matters far more than the acceptance strategy. Adjacent
/// swaps — the obvious first choice, and what this used to do — move a job only
/// one position per improving step, so a job that belongs ten places earlier is
/// unreachable unless every position on the way there also improves; on a
/// tardiness objective it usually does not, and the descent stalls after a
/// handful of its budget. Insertion reaches that position in one move. See
/// ADR 0008.
/// </para>
/// <para>
/// The acceptance rule matters at scale, which ADR 0008 did not test: a pass is
/// n·(n−1) neighbours, so at 100 jobs steepest descent spends the entire default
/// budget comparing and adopts one move, leaving three quarters of the sequence
/// never lifted out at all. First improvement adopts immediately and restarts the
/// pass, and measured better penalties at every size from 20 jobs upwards for the
/// same number of dispatches. It is the default; steepest descent stays available
/// through <see cref="SchedulingParameters.LocalSearchAcceptance"/>. See ADR 0022.
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
        int maxSteps,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(startOrder);
        ArgumentNullException.ThrowIfNull(startSchedule);
        ArgumentNullException.ThrowIfNull(startEvaluation);

        var evaluator = EvaluatorFor(scheduler, dueByJob);
        var workspace = SchedulingWorkspace.For(context, dueByJob);
        var order = startOrder.ToArray();

        var start = new ScheduleScore(
            startEvaluation.MakespanSeconds,
            startEvaluation.TotalTardinessSeconds,
            startEvaluation.LateJobCount);

        var outcome = Descend(
            evaluator, context, workspace, order, start,
            maxSteps, context.Parameters.LocalSearchAcceptance, cancellationToken);

        if (outcome.StepsUsed == 0 || !outcome.Improved)
            return new LocalSearchResult([.. order], startSchedule, startEvaluation, outcome.StepsUsed);

        evaluator.Score(context, order, workspace, cancellationToken);
        var schedule = evaluator.Materialise(context, order, workspace);
        return new LocalSearchResult([.. order], schedule, ScheduleEvaluator.Evaluate(schedule, context), outcome.StepsUsed);
    }

    /// <summary>An evaluator for <paramref name="scheduler"/>, adapting one that cannot score in place.</summary>
    internal static IOrderEvaluator EvaluatorFor(IScheduler scheduler, IReadOnlyDictionary<int, long> dueByJob) =>
        scheduler as IOrderEvaluator ?? new MaterialisingEvaluator(scheduler, dueByJob);

    /// <summary>What one descent achieved, without materialising anything.</summary>
    /// <param name="Score">The best score reached.</param>
    /// <param name="Penalty">Its penalty under the context's parameters.</param>
    /// <param name="StepsUsed">Neighbours evaluated.</param>
    /// <param name="Improved">Whether the descent moved away from the starting order at all.</param>
    internal readonly record struct DescentOutcome(ScheduleScore Score, double Penalty, int StepsUsed, bool Improved);

    /// <summary>
    /// Descends from <paramref name="order"/>, leaving the best order found in it.
    /// Allocates nothing per candidate.
    /// </summary>
    internal static DescentOutcome Descend(
        IOrderEvaluator evaluator,
        SchedulingContext context,
        SchedulingWorkspace workspace,
        int[] order,
        ScheduleScore startScore,
        int maxSteps,
        LocalSearchAcceptance acceptance,
        CancellationToken cancellationToken)
    {
        var parameters = context.Parameters;
        var bestScore = startScore;
        double bestPenalty = startScore.Penalty(parameters);

        int n = order.Length;
        int steps = 0;
        bool everImproved = false;
        if (n < 2 || maxSteps <= 0)
            return new DescentOutcome(bestScore, bestPenalty, 0, false);

        var candidate = new int[n];
        var winner = acceptance == LocalSearchAcceptance.FirstImprovement ? null : new int[n];

        bool improved = true;
        while (improved && steps < maxSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            improved = false;

            bool passFound = false;
            double passPenalty = bestPenalty;
            var passScore = bestScore;

            for (int from = 0; from < n && steps < maxSteps; from++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool jobFound = false;
                bool adoptedNow = false;
                double jobPenalty = bestPenalty;
                var jobScore = bestScore;

                for (int to = 0; to < n && steps < maxSteps; to++)
                {
                    if (from == to)
                        continue;

                    Reinsert(order, candidate, from, to);
                    steps++;

                    var score = evaluator.Score(context, candidate, workspace, cancellationToken);
                    double penalty = score.Penalty(parameters);

                    switch (acceptance)
                    {
                        // Adopt immediately and carry on with the next source
                        // position rather than restarting the sweep. Restarting
                        // spends the budget re-testing the positions at the front
                        // of the sequence and never reaches the back — the same
                        // failure as steepest descent, one pass deeper.
                        case LocalSearchAcceptance.FirstImprovement when penalty < bestPenalty - Epsilon:
                            Array.Copy(candidate, order, n);
                            bestPenalty = penalty;
                            bestScore = score;
                            improved = true;
                            everImproved = true;
                            adoptedNow = true;
                            break;

                        // Hold the best position for this job until its row of the
                        // neighbourhood is finished, then take it.
                        case LocalSearchAcceptance.BestInsertion when penalty < jobPenalty - Epsilon:
                            Array.Copy(candidate, winner!, n);
                            jobPenalty = penalty;
                            jobScore = score;
                            jobFound = true;
                            break;

                        case LocalSearchAcceptance.SteepestDescent when penalty < passPenalty - Epsilon:
                            Array.Copy(candidate, winner!, n);
                            passPenalty = penalty;
                            passScore = score;
                            passFound = true;
                            break;

                        default:
                            break;
                    }

                    if (adoptedNow)
                        break;
                }

                if (jobFound)
                {
                    Array.Copy(winner!, order, n);
                    bestPenalty = jobPenalty;
                    bestScore = jobScore;
                    improved = true;
                    everImproved = true;
                }
            }

            if (passFound)
            {
                Array.Copy(winner!, order, n);
                bestPenalty = passPenalty;
                bestScore = passScore;
                improved = true;
                everImproved = true;
            }
        }

        return new DescentOutcome(bestScore, bestPenalty, steps, everImproved);
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

    /// <summary>
    /// Makes an <see cref="IScheduler"/> that only knows how to build schedules
    /// usable by the search, at the cost the search was written to avoid: one
    /// materialised <see cref="Schedule"/> and one full evaluation per candidate.
    /// </summary>
    private sealed class MaterialisingEvaluator(IScheduler scheduler, IReadOnlyDictionary<int, long> dueByJob) : IOrderEvaluator
    {
        private Schedule? _last;

        public ScheduleScore Score(
            SchedulingContext context, ReadOnlySpan<int> order, SchedulingWorkspace workspace, CancellationToken cancellationToken = default)
        {
            _last = scheduler.Run(context, order.ToArray(), dueByJob, cancellationToken);
            return ScheduleEvaluator.Score(_last);
        }

        public Schedule Materialise(SchedulingContext context, ReadOnlySpan<int> order, SchedulingWorkspace workspace) =>
            _last ?? scheduler.Run(context, order.ToArray(), dueByJob);
    }
}
