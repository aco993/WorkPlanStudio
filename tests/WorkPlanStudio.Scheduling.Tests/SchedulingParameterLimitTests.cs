namespace WorkPlanStudio.Scheduling.Tests;

public class SchedulingParameterLimitTests
{
    [Fact]
    public void Limit_boundaries_are_accepted()
    {
        // Each search factor at its own maximum, with the other at its minimum:
        // the two maxima are no longer jointly acceptable, because their product
        // is what costs (see Their_product_is_bounded_too).
        var maxRestarts = new SchedulingParameters
        {
            MultiStartRuns = SchedulingParameterLimits.MaxMultiStartRuns,
            LocalSearchMaxSteps = SchedulingParameterLimits.MinLocalSearchSteps,
            MinutesPerWorkingDay = SchedulingParameterLimits.MaxMinutesPerWorkingDay,
            TwkFlowFactor = SchedulingParameterLimits.MaxTwkFlowFactor,
            NopSecondsPerOp = SchedulingParameterLimits.MaxDueDateSeconds,
            SlackSeconds = SchedulingParameterLimits.MaxDueDateSeconds,
            ConstantAllowanceSeconds = SchedulingParameterLimits.MaxDueDateSeconds
        };

        SchedulingParameterLimits.Validate(maxRestarts);
        SchedulingParameterLimits.Validate(maxRestarts with
        {
            MultiStartRuns = SchedulingParameterLimits.MinMultiStartRuns,
            LocalSearchMaxSteps = SchedulingParameterLimits.MaxLocalSearchSteps
        });
    }

    /// <summary>
    /// The factors bound each other: 64 restarts of 20 000 steps is 1.28 million
    /// dispatches, which the per-factor limits alone declared acceptable.
    /// </summary>
    [Fact]
    public void Their_product_is_bounded_too()
    {
        var parameters = new SchedulingParameters
        {
            MultiStartRuns = SchedulingParameterLimits.MaxMultiStartRuns,
            LocalSearchMaxSteps = SchedulingParameterLimits.MaxLocalSearchSteps
        };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => SchedulingParameterLimits.Validate(parameters));
        Assert.Contains("MultiStartRuns × LocalSearchMaxSteps", error.Message, StringComparison.Ordinal);

        // Exactly at the cap is still fine.
        SchedulingParameterLimits.Validate(new SchedulingParameters
        {
            MultiStartRuns = 10,
            LocalSearchMaxSteps = SchedulingParameterLimits.MaxTotalEvaluations / 10
        });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(65, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 20001)]
    public void Extreme_search_budgets_are_rejected(int multiStart, int localSearch)
    {
        var parameters = new SchedulingParameters
        {
            MultiStartRuns = multiStart,
            LocalSearchMaxSteps = localSearch
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => SchedulingParameterLimits.Validate(parameters));
    }

    [Fact]
    public void Cancellation_is_observed_before_scheduling_work()
    {
        var context = SearchTests.MediumScenario(DispatchRule.Fifo);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new SchedulingEngine().RunCancellable(context, cancellation.Token));
    }

    /// <summary>
    /// A duration the machine clock cannot hold is rejected at the boundary, not
    /// carried into the timeline. It used to survive as long as the per-job sum
    /// did not overflow — and the shared clock, where jobs compose, then wrapped
    /// to a negative total tardiness that beats every feasible schedule.
    /// </summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue / 2 + 10)]
    [InlineData(SchedulingParameterLimits.MaxStepDurationSeconds + 1)]
    public void Context_rejects_a_single_step_longer_than_the_limit(long duration)
    {
        var job = new ProductionJob
        {
            Id = 1,
            Reference = "overflow",
            Steps = [new JobStep(1, 1, duration)]
        };

        var error = Assert.Throws<ArgumentException>(() => new SchedulingContext(
            [job],
            [new MachineCapacity(1, "Center")],
            new SchedulingParameters()));
        Assert.Contains("limit on a single operation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_rejects_processing_time_sum_overflow()
    {
        var job = new ProductionJob
        {
            Id = 1,
            Reference = "overflow",
            Steps =
            [
                new JobStep(1, 1, long.MaxValue),
                new JobStep(2, 1, 1)
            ]
        };

        Assert.Throws<ArgumentException>(() => new SchedulingContext(
            [job],
            [new MachineCapacity(1, "Center")],
            new SchedulingParameters()));
    }

    /// <summary>
    /// The steps are each inside the per-operation limit, but they compose past
    /// the horizon — which is the number the timeline is actually bounded by.
    /// </summary>
    [Fact]
    public void Context_rejects_an_instance_that_outgrows_the_horizon()
    {
        long perStep = SchedulingParameterLimits.MaxStepDurationSeconds;
        int stepsNeeded = (int)(SchedulingParameterLimits.MaxHorizonSeconds / perStep) + 2;

        var jobs = Enumerable.Range(1, stepsNeeded)
            .Select(id => new ProductionJob
            {
                Id = id,
                Reference = $"J{id}",
                Steps = [new JobStep(1, 1, perStep)]
            })
            .ToArray();

        var error = Assert.Throws<ArgumentException>(() => new SchedulingContext(
            jobs,
            [new MachineCapacity(1, "Center")],
            new SchedulingParameters()));
        Assert.Contains("planning horizon", error.Message, StringComparison.Ordinal);
    }
}
