namespace WorkPlanStudio.Scheduling.Tests;

public class EdgeCaseTests
{
    [Fact]
    public void Empty_job_list_yields_an_empty_schedule()
    {
        var ctx = new SchedulingContext([], new[] { Machine(1) }, new SchedulingParameters());

        var result = new SchedulingEngine().Run(ctx);

        Assert.Empty(result.Schedule.Operations);
        Assert.Equal(0, result.Schedule.MakespanSeconds);
        Assert.Equal(0, result.Evaluation.LateJobCount);
        Assert.Equal(1.0, result.Evaluation.OnTimeRate, 6);
    }

    [Fact]
    public void Single_operation_completes_at_its_duration()
    {
        var ctx = Context(new SchedulingParameters(), new[] { Machine(1) }, Job(1, Step(10, 1, 420)));

        var result = new SchedulingEngine().Run(ctx);

        Assert.Equal(420, result.Schedule.MakespanSeconds);
        Feasibility.AssertFeasible(result.Schedule, ctx);
    }

    [Fact]
    public void A_job_without_steps_is_rejected()
    {
        var bad = new ProductionJob { Id = 1, Reference = "J1", Steps = [] };
        Assert.Throws<ArgumentException>(() =>
            new SchedulingContext([bad], new[] { Machine(1) }, new SchedulingParameters()));
    }

    [Fact]
    public void A_step_on_an_unknown_work_center_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Context(new SchedulingParameters(), new[] { Machine(1) }, Job(1, Step(10, 99, 100))));
    }

    [Fact]
    public void Non_positive_capacity_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new SchedulingContext([Job(1, Step(10, 1, 100))], new[] { new MachineCapacity(1, "x", 0) }, new SchedulingParameters()));
    }

    [Fact]
    public void Out_of_order_step_numbers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Context(new SchedulingParameters(), new[] { Machine(1) }, Job(1, Step(20, 1, 100), Step(10, 1, 50))));
    }
}
