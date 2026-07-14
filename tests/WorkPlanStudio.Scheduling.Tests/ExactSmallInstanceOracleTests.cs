namespace WorkPlanStudio.Scheduling.Tests;

public sealed class ExactSmallInstanceOracleTests
{
    [Fact]
    public void Heuristic_matches_the_exhaustive_optimum_for_a_small_reference_instance()
    {
        var parameters = new SchedulingParameters
        {
            DueDateRule = DueDateRule.Explicit,
            DispatchRule = DispatchRule.EarliestDueDate,
            MultiStartRuns = 8,
            LocalSearchMaxSteps = 100,
            Seed = 20260714
        };
        var context = Scenario.Context(parameters, [Scenario.Machine(1)],
            Scenario.DueAt(1, 1, Scenario.Step(1, 1, 1)),
            Scenario.DueAt(2, 3, Scenario.Step(1, 1, 2)),
            Scenario.DueAt(3, 6, Scenario.Step(1, 1, 3)));
        var exact = ExactDispatchOrderOptimizer.Run(context, TestContext.Current.CancellationToken);

        var heuristic = new SchedulingEngine().Run(context);

        Assert.True(exact.IsOptimalWithinDispatchOrderModel);
        Assert.Equal(6, exact.EvaluatedOrders);
        Assert.Equal(exact.Result.Evaluation.Penalty, heuristic.Evaluation.Penalty, precision: 10);
    }

    [Fact]
    public void Exact_optimizer_handles_empty_rejects_unsafe_size_and_observes_cancellation()
    {
        var empty = ExactDispatchOrderOptimizer.Run(
            new SchedulingContext([], [], new SchedulingParameters()), TestContext.Current.CancellationToken);
        Assert.Equal(1, empty.EvaluatedOrders);
        Assert.Empty(empty.Result.Schedule.Jobs);

        var jobs = Enumerable.Range(1, ExactDispatchOrderOptimizer.MaxJobs + 1)
            .Select(id => Scenario.Job(id, Scenario.Step(1, 1, 1)))
            .ToArray();
        var oversized = Scenario.Context(new SchedulingParameters(), [Scenario.Machine(1)], jobs);
        Assert.Throws<ArgumentOutOfRangeException>(() => ExactDispatchOrderOptimizer.Run(
            oversized, TestContext.Current.CancellationToken));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ExactDispatchOrderOptimizer.Run(
            Scenario.Context(new SchedulingParameters(), [Scenario.Machine(1)], jobs[..2]), cancelled.Token));
    }
}
