namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The exhaustive optimizer. Its value is that it is exact within a stated model,
/// so these tests pin both the exactness and the limits of the claim.
/// </summary>
public class ExactDispatchOrderOptimizerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void It_evaluates_every_order()
    {
        var context = Context(RuleOnly(DispatchRule.Fifo), [Machine(1)],
            Job(1, Step(10, 1, 100)), Job(2, Step(10, 1, 200)), Job(3, Step(10, 1, 300)));

        Assert.Equal(6, ExactDispatchOrderOptimizer.Run(context, Ct).EvaluatedOrders);   // 3! = 6
    }

    [Fact]
    public void It_finds_the_order_the_heuristic_would_have_to_search_for()
    {
        // The urgent job is last in the natural order and only a reordering saves it.
        var context = Context(
            new SchedulingParameters { DueDateRule = DueDateRule.Explicit, MultiStartRuns = 1, LocalSearchMaxSteps = 0 },
            [Machine(1)],
            DueAt(1, 100_000, Step(10, 1, 1000)),
            DueAt(2, 100_000, Step(10, 1, 1000)),
            DueAt(3, 1_100, Step(10, 1, 1000)));

        var exact = ExactDispatchOrderOptimizer.Run(context, Ct);

        Assert.Equal(0, exact.Result.Evaluation.LateJobCount);
    }

    [Fact]
    public void No_reachable_order_beats_it()
    {
        var context = SearchTests.MediumScenario(DispatchRule.EarliestDueDate);
        var due = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();

        double exact = ExactDispatchOrderOptimizer.Run(context, Ct).Result.Evaluation.Penalty;

        // Sample the space independently: nothing may score better than "exact".
        for (int seed = 1; seed <= 50; seed++)
        {
            var order = Enumerable.Range(0, context.Jobs.Count).ToArray();
            new DeterministicRandom(seed).Shuffle(order);
            double sampled = ScheduleEvaluator.Evaluate(scheduler.Run(context, order, due), context).Penalty;

            Assert.True(sampled >= exact - 1e-9, $"seed {seed} found {sampled} < exact {exact}");
        }
    }

    [Fact]
    public void It_refuses_instances_it_cannot_enumerate()
    {
        var jobs = Enumerable.Range(1, ExactDispatchOrderOptimizer.MaxJobs + 1)
            .Select(i => Job(i, Step(10, 1, 100)))
            .ToArray();
        var context = Context(RuleOnly(DispatchRule.Fifo), [Machine(1)], jobs);

        Assert.False(ExactDispatchOrderOptimizer.CanEnumerate(context.Jobs.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExactDispatchOrderOptimizer.Run(context, Ct));
    }

    [Fact]
    public void An_empty_instance_is_handled()
    {
        var result = ExactDispatchOrderOptimizer.Run(
            new SchedulingContext([], [Machine(1)], new SchedulingParameters()), Ct);

        Assert.Empty(result.Result.Schedule.Operations);
        Assert.Equal(1, result.EvaluatedOrders);
    }

    [Fact]
    public void It_is_deterministic()
    {
        var a = ExactDispatchOrderOptimizer.Run(SearchTests.MediumScenario(DispatchRule.LongestProcessingTime), Ct);
        var b = ExactDispatchOrderOptimizer.Run(SearchTests.MediumScenario(DispatchRule.LongestProcessingTime), Ct);

        Assert.Equal(a.Result.Schedule.Signature(), b.Result.Schedule.Signature());
    }

    [Fact]
    public void It_honours_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ExactDispatchOrderOptimizer.Run(SearchTests.MediumScenario(DispatchRule.Fifo), cancelled.Token));
    }
}
