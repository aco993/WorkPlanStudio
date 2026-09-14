namespace WorkPlanStudio.Scheduling.Tests;

public class SearchTests
{
    /// <summary>A non-trivial shared fixture: 4 multi-step jobs over 3 work centers (one with two slots).</summary>
    internal static SchedulingContext MediumScenario(
        DispatchRule rule, int multiStart = 8, int localSearch = 2000, int seed = 20260616)
    {
        var machines = new[] { Machine(1), Machine(2, capacity: 2), Machine(3) };
        var p = new SchedulingParameters
        {
            DispatchRule = rule,
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 1.5,
            MultiStartRuns = multiStart,
            LocalSearchMaxSteps = localSearch,
            Seed = seed
        };
        var jobs = new[]
        {
            Released(1, 0, Step(10, 1, 300), Step(20, 2, 200), Step(30, 3, 150)),
            Released(2, 0, Step(10, 2, 250), Step(20, 1, 180)),
            Released(3, 100, Step(10, 3, 400), Step(20, 2, 120), Step(30, 1, 90)),
            Released(4, 50, Step(10, 1, 150), Step(20, 3, 220)),
        };
        return Context(p, machines, jobs);
    }

    [Fact]
    public void Local_search_improves_a_deliberately_bad_starting_order()
    {
        var machines = new[] { Machine(1) };
        var ctx = Context(new SchedulingParameters(), machines,
            DueAt(1, 100_000, Step(10, 1, 200)),   // loose, long
            DueAt(2, 150, Step(10, 1, 100)));        // urgent, short
        var due = new Dictionary<int, long> { [1] = 100_000, [2] = 150 };
        var scheduler = new DispatchScheduler();

        var startOrder = new[] { 0, 1 };             // loose first → urgent finishes late
        var startSchedule = scheduler.Run(ctx, startOrder, due, Ct);
        var startEval = ScheduleEvaluator.Evaluate(startSchedule, ctx);

        var result = LocalSearch.Improve(scheduler, ctx, due, startOrder, startSchedule, startEval, maxSteps: 100, Ct);

        Assert.True(result.Evaluation.Penalty < startEval.Penalty);
        Assert.Equal(0, result.Schedule.Operations.Single(o => o.JobId == 2).StartSeconds); // urgent now first
    }

    [Fact]
    public void Local_search_with_zero_steps_is_a_no_op()
    {
        var ctx = MediumScenario(DispatchRule.Fifo);
        var due = DueDateAssigner.Assign(ctx);
        var scheduler = new DispatchScheduler();
        var order = PriorityOrdering.For(ctx, due);
        var schedule = scheduler.Run(ctx, order, due, Ct);
        var eval = ScheduleEvaluator.Evaluate(schedule, ctx);

        var result = LocalSearch.Improve(scheduler, ctx, due, order, schedule, eval, maxSteps: 0, Ct);

        Assert.Equal(0, result.StepsUsed);
        Assert.Equal(schedule.Signature(), result.Schedule.Signature());
        Assert.Equal(eval.Penalty, result.Evaluation.Penalty, 9);
    }

    [Fact]
    public void Local_search_never_returns_a_worse_schedule()
    {
        var ctx = MediumScenario(DispatchRule.LongestProcessingTime);
        var due = DueDateAssigner.Assign(ctx);
        var scheduler = new DispatchScheduler();
        var order = PriorityOrdering.For(ctx, due);
        var schedule = scheduler.Run(ctx, order, due, Ct);
        var eval = ScheduleEvaluator.Evaluate(schedule, ctx);

        var result = LocalSearch.Improve(scheduler, ctx, due, order, schedule, eval, maxSteps: 500, Ct);

        Assert.True(result.Evaluation.Penalty <= eval.Penalty + 1e-9);
    }

    [Fact]
    public void Engine_result_is_never_worse_than_the_pure_rule_order()
    {
        foreach (var rule in Enum.GetValues<DispatchRule>())
        {
            var ctx = MediumScenario(rule);
            var due = DueDateAssigner.Assign(ctx);
            double rulePenalty = ScheduleEvaluator
                .Evaluate(new DispatchScheduler().Run(ctx, PriorityOrdering.For(ctx, due), due, Ct), ctx).Penalty;

            var result = new SchedulingEngine().Run(ctx);

            Assert.True(result.Evaluation.Penalty <= rulePenalty + 1e-9, $"rule {rule}");
        }
    }

    [Fact]
    public void More_starts_never_hurt()
    {
        var one = new SchedulingEngine().Run(MediumScenario(DispatchRule.Fifo, multiStart: 1, localSearch: 0));
        var many = new SchedulingEngine().Run(MediumScenario(DispatchRule.Fifo, multiStart: 16, localSearch: 0));
        Assert.True(many.Evaluation.Penalty <= one.Evaluation.Penalty + 1e-9);
    }

    /// <summary>
    /// "Never hurt" is true by construction — restart 0 is always the rule order
    /// and ties are kept — so on its own it cannot tell a working multi-start from
    /// a loop that does nothing. This is the other half: over a fixed set of
    /// instances, eight restarts must find a <b>strictly</b> better schedule than
    /// one on at least one of them, or the default is paying eight times over for
    /// a guarantee it already had.
    /// </summary>
    [Fact]
    public void More_starts_sometimes_strictly_help()
    {
        int helped = 0;
        var worse = new List<int>();

        foreach (int seed in RestartFixtureSeeds)
        {
            double one = new SchedulingEngine().Run(RestartFixture(seed, multiStart: 1)).Evaluation.Penalty;
            double many = new SchedulingEngine().Run(RestartFixture(seed, multiStart: 8)).Evaluation.Penalty;

            if (many < one - 1e-9) helped++;
            if (many > one + 1e-9) worse.Add(seed);
        }

        Assert.Empty(worse);
        Assert.True(helped > 0,
            $"eight restarts matched one restart on all {RestartFixtureSeeds.Length} fixtures — the extra seven bought nothing");
    }

    /// <summary>
    /// And the gain has to survive to the end: the restart that found it must be
    /// the one the engine keeps.
    /// </summary>
    [Fact]
    public void The_best_restart_is_the_one_reported()
    {
        foreach (int seed in RestartFixtureSeeds)
        {
            var context = RestartFixture(seed, multiStart: 8);
            var result = new SchedulingEngine().Run(context);

            double best = double.PositiveInfinity;
            for (int starts = 1; starts <= 8; starts++)
                best = Math.Min(best, new SchedulingEngine().Run(RestartFixture(seed, starts)).Evaluation.Penalty);

            Assert.Equal(best, result.Evaluation.Penalty, 9);
        }
    }

    private static readonly int[] RestartFixtureSeeds = [1, 3, 7, 19, 42, 55];

    /// <summary>
    /// Seven jobs on four work centers — small enough that a descent converges
    /// well inside the budget, so a restart is the only thing left that can move
    /// the incumbent. This is the size where multi-start earns its keep; at 100
    /// jobs the budget runs out long before the descent converges and the extra
    /// restarts measure nothing.
    /// </summary>
    private static SchedulingContext RestartFixture(int seed, int multiStart)
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
            DispatchRule = DispatchRule.Fifo,
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 1.5,
            MultiStartRuns = multiStart,
            Seed = seed
        });
    }
}
