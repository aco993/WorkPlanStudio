namespace WorkPlanStudio.Scheduling.Tests;

public class SchedulingParameterLimitTests
{
    [Fact]
    public void Limit_boundaries_are_accepted()
    {
        var parameters = new SchedulingParameters
        {
            MultiStartRuns = SchedulingParameterLimits.MaxMultiStartRuns,
            LocalSearchMaxSteps = SchedulingParameterLimits.MaxLocalSearchSteps,
            MinutesPerWorkingDay = SchedulingParameterLimits.MaxMinutesPerWorkingDay,
            TwkFlowFactor = SchedulingParameterLimits.MaxTwkFlowFactor,
            NopSecondsPerOp = SchedulingParameterLimits.MaxDueDateSeconds,
            SlackSeconds = SchedulingParameterLimits.MaxDueDateSeconds,
            ConstantAllowanceSeconds = SchedulingParameterLimits.MaxDueDateSeconds
        };

        SchedulingParameterLimits.Validate(parameters);
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

        Assert.Throws<OverflowException>(() => new SchedulingContext(
            [job],
            [new MachineCapacity(1, "Center")],
            new SchedulingParameters()));
    }
}
