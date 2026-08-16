namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The engine is a heuristic, so "it returns a feasible schedule" is a weak
/// assertion and "it never makes things worse" is barely stronger. On instances
/// small enough to enumerate exhaustively (n! job orders) the true optimum is
/// computable, which turns schedule quality into something that can be tested
/// rather than claimed.
/// </summary>
public class OptimalityTests
{
    /// <summary>The best penalty achievable for this instance, over every job order.</summary>
    private static double BruteForceOptimum(SchedulingContext context)
    {
        var due = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();
        double best = double.MaxValue;

        foreach (var order in Permutations([.. Enumerable.Range(0, context.Jobs.Count)]))
            best = Math.Min(best, ScheduleEvaluator.Evaluate(scheduler.Run(context, order, due), context).Penalty);

        return best;
    }

    private static IEnumerable<int[]> Permutations(int[] items, int fixedPrefix = 0)
    {
        if (fixedPrefix == items.Length)
        {
            yield return (int[])items.Clone();
            yield break;
        }

        for (int i = fixedPrefix; i < items.Length; i++)
        {
            (items[fixedPrefix], items[i]) = (items[i], items[fixedPrefix]);
            foreach (var permutation in Permutations(items, fixedPrefix + 1))
                yield return permutation;
            (items[fixedPrefix], items[i]) = (items[i], items[fixedPrefix]);
        }
    }

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

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(101)]
    [InlineData(20260616)]
    public void Engine_finds_the_optimum_on_enumerable_instances(int seed)
    {
        var context = RandomInstance(seed);

        double found = new SchedulingEngine().Run(context).Evaluation.Penalty;
        double optimum = BruteForceOptimum(context);

        Assert.Equal(optimum, found, 6);
    }

    [Fact]
    public void Engine_stays_within_a_few_percent_of_the_optimum_under_every_rule()
    {
        foreach (var rule in Enum.GetValues<DispatchRule>())
        {
            foreach (int seed in new[] { 3, 19, 55 })
            {
                var context = RandomInstance(seed, rule);

                double found = new SchedulingEngine().Run(context).Evaluation.Penalty;
                double optimum = BruteForceOptimum(context);
                double gap = optimum <= 0 ? 0 : (found - optimum) / optimum;

                Assert.True(gap <= 0.05, $"{rule} seed {seed}: penalty {found:F2} vs optimum {optimum:F2} ({gap:P1} gap)");
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
        var startSchedule = scheduler.Run(context, start, due);
        var startEvaluation = ScheduleEvaluator.Evaluate(startSchedule, context);

        var result = LocalSearch.Improve(scheduler, context, due, start, startSchedule, startEvaluation, 1000);

        Assert.Equal(0, result.Evaluation.LateJobCount);
        Assert.Equal(4, result.Order[0]);   // the urgent job moved from last to first in one insertion
    }
}
