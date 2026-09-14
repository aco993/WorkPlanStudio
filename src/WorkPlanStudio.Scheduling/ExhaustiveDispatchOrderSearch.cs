namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Evaluates every job priority order exhaustively and returns the best schedule
/// the dispatcher can produce.
/// <para>
/// Be precise about what this proves. It is exact <b>within the dispatch-order
/// model</b>: of all <c>n!</c> orders the <see cref="DispatchScheduler"/> can be
/// handed, this returns the one with the lowest penalty. It is <b>not</b> a
/// general job-shop optimality proof — the dispatcher places each job's steps
/// greedily and never back-fills idle gaps, so schedules outside that model are
/// not considered and can be strictly better. On a two-job, three-machine
/// instance the gap to the true optimum is 50 %.
/// </para>
/// <para>
/// It exists for two reasons: as an answer for small real instances, where "the
/// best possible sequence" is computable and worth having; and as the reference
/// the heuristic is measured against in <c>OptimalityTests</c>.
/// </para>
/// </summary>
public static class ExhaustiveDispatchOrderSearch
{
    /// <summary>
    /// Largest instance this will attempt. 9! = 362 880 dispatches is roughly a
    /// second; 10! would be ten times that, and 12! would not return.
    /// </summary>
    public const int MaxJobs = 9;

    /// <summary>True when <paramref name="jobCount"/> is small enough to enumerate.</summary>
    public static bool CanEnumerate(int jobCount) => jobCount <= MaxJobs;

    /// <summary>Evaluates every job order and returns the best, with the count of orders tried.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The instance is too large to enumerate.</exception>
    public static DispatchOrderSearchResult Run(SchedulingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!CanEnumerate(context.Jobs.Count))
            throw new ArgumentOutOfRangeException(
                nameof(context),
                $"Exhaustive dispatch-order search supports at most {MaxJobs} jobs; this instance has {context.Jobs.Count}.");

        var dueByJob = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();
        var workspace = SchedulingWorkspace.For(context, dueByJob);

        if (context.Jobs.Count == 0)
        {
            var empty = scheduler.Materialise(context, [], workspace);
            return new DispatchOrderSearchResult(
                new SchedulingResult(empty, ScheduleEvaluator.Evaluate(empty, context), dueByJob, 0)
                {
                    EquivalentRules = PriorityOrdering.EquivalentRules(context, dueByJob)
                },
                EvaluatedOrders: 1);
        }

        var order = Enumerable.Range(0, context.Jobs.Count).ToArray();
        var bestOrder = (int[])order.Clone();
        double bestPenalty = double.PositiveInfinity;
        long evaluated = 0;

        Enumerate(0);

        // One schedule object for the whole enumeration, built from the winner.
        scheduler.Score(context, bestOrder, workspace, cancellationToken);
        var bestSchedule = scheduler.Materialise(context, bestOrder, workspace);

        return new DispatchOrderSearchResult(
            new SchedulingResult(bestSchedule, ScheduleEvaluator.Evaluate(bestSchedule, context), dueByJob, 0)
            {
                EquivalentRules = PriorityOrdering.EquivalentRules(context, dueByJob)
            },
            evaluated);

        void Enumerate(int fixedPrefix)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fixedPrefix == order.Length)
            {
                double penalty = scheduler.Score(context, order, workspace, cancellationToken).Penalty(context.Parameters);
                evaluated++;

                // Deterministic because the enumeration is: strict improvement
                // keeps whichever of several equally good orders the swap
                // recursion reaches first. That is a fixed order for a fixed
                // instance, but it is not lexicographic and it is not a
                // tie-break rule anyone should reason about.
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    Array.Copy(order, bestOrder, order.Length);
                }

                return;
            }

            for (int candidate = fixedPrefix; candidate < order.Length; candidate++)
            {
                (order[fixedPrefix], order[candidate]) = (order[candidate], order[fixedPrefix]);
                Enumerate(fixedPrefix + 1);
                (order[fixedPrefix], order[candidate]) = (order[candidate], order[fixedPrefix]);
            }
        }
    }
}

/// <summary>
/// The best schedule reachable by reordering jobs, and how many orders were tried.
/// </summary>
/// <param name="Result">The winning schedule and its evaluation.</param>
/// <param name="EvaluatedOrders">Complete job orders evaluated — <c>n!</c> for a finished run.</param>
public sealed record DispatchOrderSearchResult(SchedulingResult Result, long EvaluatedOrders);
