using WorkPlanStudio.Scheduling.Exact;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The engine is a heuristic, so "it returns a feasible schedule" is a weak
/// assertion and "it never makes things worse" is barely stronger. These tests
/// measure it against two references and are careful about which one is which.
/// <list type="bullet">
/// <item><b>The true optimum</b> comes from <see cref="ExactJobShopSolver"/>,
/// which shares the problem definition with the engine and none of the machinery
/// that turns a problem into a schedule. That is what makes it an oracle.</item>
/// <item><b>The best dispatch order</b> comes from
/// <see cref="ExactDispatchOrderOptimizer"/>, which enumerates every job order and
/// hands each to the same dispatcher the engine uses. It is a bound on the
/// <i>search</i> and nothing else.</item>
/// </list>
/// <para>
/// This file used to use the second one for both jobs. Both sides of the assertion
/// then ran the same <c>DispatchScheduler</c> and the same <c>ScheduleEvaluator</c>,
/// so any bug in placement or scoring cancelled out exactly, and the suite could
/// only ever catch a search-order bug — the one class of bug it was not advertised
/// to catch. The distinction is not academic: on the instances below the engine is
/// on the best dispatch order every time and between 14 % and 80 % above the
/// optimum.
/// </para>
/// </summary>
public class OptimalityTests
{
    /// <summary>The optimum of the scheduling problem, proved rather than assumed.</summary>
    private static double TrueOptimum(SchedulingContext context) =>
        ExactJobShopSolver.Solve(context, cancellationToken: Ct).OptimalPenalty;

    /// <summary>The lowest penalty any job order can produce through the dispatcher.</summary>
    private static double BestDispatchOrder(SchedulingContext context) =>
        ExactDispatchOrderOptimizer.Run(context, Ct).Result.Evaluation.Penalty;

    private static double EnginePenalty(SchedulingContext context) =>
        new SchedulingEngine().RunCancellable(context, Ct).Evaluation.Penalty;

    /// <summary>Deterministic pseudo-random 7-job instances, built from the engine's own PRNG.</summary>
    private static SchedulingContext RandomInstance(int seed, DispatchRule rule = DispatchRule.EarliestDueDate)
    {
        var rng = new DeterministicRandom(seed);
        var machines = Enumerable.Range(1, 4).Select(id => Machine(id)).ToArray();

        var jobs = new ProductionJob[7];
        for (int j = 0; j < jobs.Length; j++)
        {
            var steps = new List<JobStep>();
            int stepCount = 2 + rng.NextInt(3);
            for (int s = 0; s < stepCount; s++)
                steps.Add(new JobStep(s + 1, 1 + rng.NextInt(machines.Length), 600 + rng.NextInt(9000)));

            jobs[j] = new ProductionJob
            {
                Id = j + 1,
                Reference = $"J{j + 1}",
                Weight = 1 + rng.NextInt(4),
                Steps = steps
            };
        }

        return new SchedulingContext(jobs, machines, new SchedulingParameters
        {
            DispatchRule = rule,
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 1.5,
            Seed = seed
        });
    }

    /// <summary>
    /// The search does its job: on every one of these the multi-start descent lands
    /// on the best order the dispatcher could have been handed, which enumerating
    /// all 5 040 of them confirms.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(101)]
    [InlineData(20260616)]
    public void Engine_finds_the_best_order_its_dispatcher_can_be_handed(int seed)
    {
        var context = RandomInstance(seed);

        Assert.Equal(BestDispatchOrder(context), EnginePenalty(context), 6);
    }

    /// <summary>
    /// The oracle property, and the one the old version of this file could not
    /// state: nothing the engine returns is better than the optimum. A schedule
    /// that beats the optimum is not a good schedule, it is a solver and an engine
    /// that disagree about the problem.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(101)]
    [InlineData(20260616)]
    public void Engine_never_beats_the_proved_optimum(int seed)
    {
        var context = RandomInstance(seed);
        double optimum = TrueOptimum(context);
        double found = EnginePenalty(context);

        Assert.True(found >= optimum - 1e-6, $"engine {found:F4} beat the proved optimum {optimum:F4}");
    }

    /// <summary>
    /// Where the engine's remaining gap comes from, as an assertion rather than a
    /// footnote: the search is exact against its own model on all five instances,
    /// and the model is more than ten per cent above the optimum on all five. No
    /// amount of search budget closes that.
    /// </summary>
    [Fact]
    public void The_gap_that_is_left_belongs_to_the_dispatch_order_model_not_to_the_search()
    {
        foreach (int seed in new[] { 1, 7, 42, 101, 20260616 })
        {
            var context = RandomInstance(seed);
            double optimum = TrueOptimum(context);
            double order = BestDispatchOrder(context);
            double found = EnginePenalty(context);

            Assert.Equal(order, found, 6);
            Assert.True((order - optimum) / optimum > 0.10,
                $"seed {seed}: the best dispatch order {order:F4} is only {(order - optimum) / optimum:P2} above the optimum {optimum:F4}");
        }
    }

    /// <summary>
    /// Three implementations, one answer. On a single work center with one
    /// operation per job the dispatch-order model is not a restriction at all —
    /// every schedule is a permutation — so the permutation enumerator, the
    /// branch-and-bound and the engine must agree exactly. They share no search
    /// code, and agreement between three of them is evidence in a way that
    /// agreement between one and itself never was.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(13)]
    [InlineData(77)]
    [InlineData(20260911)]
    public void Three_implementations_agree_where_their_models_coincide(int seed)
    {
        var rng = new DeterministicRandom(seed);
        var jobs = new ProductionJob[6];
        for (int j = 0; j < jobs.Length; j++)
        {
            jobs[j] = new ProductionJob
            {
                Id = j + 1,
                Reference = $"J{j + 1}",
                ReleaseSeconds = rng.NextInt(4) * 900L,
                Weight = 1 + rng.NextInt(4),
                Steps = [new JobStep(10, 1, 600 + rng.NextInt(6) * 600L)]
            };
        }

        var context = new SchedulingContext(jobs, [Machine(1)], new SchedulingParameters
        {
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 1.5,
            Seed = seed
        });

        double optimum = TrueOptimum(context);
        Assert.Equal(optimum, BestDispatchOrder(context), 6);
        Assert.Equal(optimum, EnginePenalty(context), 6);
    }

    /// <summary>
    /// The old five-per-cent guard, with the reference it always actually used
    /// named in the method. It guards the search, which is what it can guard.
    /// </summary>
    [Fact]
    public void Engine_stays_within_a_few_percent_of_the_best_dispatch_order_under_every_rule()
    {
        foreach (var rule in Enum.GetValues<DispatchRule>())
        {
            foreach (int seed in new[] { 3, 19, 55 })
            {
                var context = RandomInstance(seed, rule);

                double found = EnginePenalty(context);
                double reference = BestDispatchOrder(context);
                double gap = reference <= 0 ? 0 : (found - reference) / reference;

                Assert.True(gap <= 0.05,
                    $"{rule} seed {seed}: penalty {found:F2} vs best order {reference:F2} ({gap:P1} gap)");
            }
        }
    }

    /// <summary>
    /// Guards the neighbourhood choice specifically. Adjacent swaps cannot move a
    /// job more than one position per improving step; this instance parks the most
    /// urgent job last, where only a multi-position move reaches the optimum.
    /// </summary>
    [Fact]
    public void Insertion_escapes_a_local_optimum_that_adjacent_swaps_cannot()
    {
        var machines = new[] { Machine(1) };
        var jobs = new[]
        {
            DueAt(1, 100_000, Step(10, 1, 1000)),
            DueAt(2, 100_000, Step(10, 1, 1000)),
            DueAt(3, 100_000, Step(10, 1, 1000)),
            DueAt(4, 100_000, Step(10, 1, 1000)),
            DueAt(5, 1_100, Step(10, 1, 1000)),   // urgent, but last in the order
        };
        var context = Context(
            new SchedulingParameters
            {
                DueDateRule = DueDateRule.Explicit,
                MultiStartRuns = 1,          // no restarts: the descent has to do the work
                LocalSearchMaxSteps = 1000
            },
            machines, jobs);

        var due = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();
        var start = new[] { 0, 1, 2, 3, 4 };
        var startSchedule = scheduler.Run(context, start, due, Ct);
        var startEvaluation = ScheduleEvaluator.Evaluate(startSchedule, context);

        var result = LocalSearch.Improve(scheduler, context, due, start, startSchedule, startEvaluation, 1000, Ct);

        Assert.Equal(0, result.Evaluation.LateJobCount);
        Assert.Equal(4, result.Order[0]);   // the urgent job moved from last to first in one insertion
    }
}
