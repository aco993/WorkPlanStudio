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
        var startSchedule = scheduler.Run(ctx, startOrder, due);
        var startEval = ScheduleEvaluator.Evaluate(startSchedule, ctx);

        var result = LocalSearch.Improve(scheduler, ctx, due, startOrder, startSchedule, startEval, maxSteps: 100);

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
        var schedule = scheduler.Run(ctx, order, due);
        var eval = ScheduleEvaluator.Evaluate(schedule, ctx);

        var result = LocalSearch.Improve(scheduler, ctx, due, order, schedule, eval, maxSteps: 0);

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
        var schedule = scheduler.Run(ctx, order, due);
        var eval = ScheduleEvaluator.Evaluate(schedule, ctx);

        var result = LocalSearch.Improve(scheduler, ctx, due, order, schedule, eval, maxSteps: 500);

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
                .Evaluate(new DispatchScheduler().Run(ctx, PriorityOrdering.For(ctx, due), due), ctx).Penalty;

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
}
