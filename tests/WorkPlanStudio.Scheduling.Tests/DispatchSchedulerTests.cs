namespace WorkPlanStudio.Scheduling.Tests;

public class DispatchSchedulerTests
{
    private static Dictionary<int, long> FarDue(SchedulingContext ctx) =>
        ctx.Jobs.ToDictionary(j => j.Id, _ => long.MaxValue / 4);

    [Fact]
    public void Single_capacity_machine_runs_jobs_serially()
    {
        var ctx = Context(new SchedulingParameters(), new[] { Machine(1, capacity: 1) },
            Job(1, Step(10, 1, 100)), Job(2, Step(10, 1, 100)), Job(3, Step(10, 1, 100)));

        var schedule = new DispatchScheduler().Run(ctx, new[] { 0, 1, 2 }, FarDue(ctx));

        Assert.Equal(300, schedule.MakespanSeconds);
        Feasibility.AssertFeasible(schedule, ctx);
    }

    [Fact]
    public void Parallel_capacity_lets_jobs_overlap()
    {
        var ctx = Context(new SchedulingParameters(), new[] { Machine(1, capacity: 2) },
            Job(1, Step(10, 1, 100)), Job(2, Step(10, 1, 100)));

        var schedule = new DispatchScheduler().Run(ctx, new[] { 0, 1 }, FarDue(ctx));

        Assert.Equal(100, schedule.MakespanSeconds); // both at once on the two slots
        Assert.Equal(new[] { 0, 1 }, schedule.Operations.Select(o => o.SlotIndex).OrderBy(s => s).ToArray());
        Feasibility.AssertFeasible(schedule, ctx);
    }

    [Fact]
    public void Jobs_on_distinct_machines_run_fully_in_parallel()
    {
        var machines = new[] { Machine(1), Machine(2), Machine(3), Machine(4) };
        var ctx = Context(new SchedulingParameters(), machines,
            Job(1, Step(10, 1, 100), Step(20, 2, 100)),
            Job(2, Step(10, 3, 100), Step(20, 4, 100)));

        var schedule = new DispatchScheduler().Run(ctx, new[] { 0, 1 }, FarDue(ctx));

        Assert.Equal(200, schedule.MakespanSeconds); // each job's own critical path
        Feasibility.AssertFeasible(schedule, ctx);
    }

    [Fact]
    public void Steps_within_a_job_respect_precedence()
    {
        var ctx = Context(new SchedulingParameters(), new[] { Machine(1), Machine(2) },
            Job(1, Step(10, 1, 100), Step(20, 2, 50)));

        var schedule = new DispatchScheduler().Run(ctx, new[] { 0 }, FarDue(ctx));
        var first = schedule.Operations.Single(o => o.StepNumber == 10);
        var second = schedule.Operations.Single(o => o.StepNumber == 20);

        Assert.Equal(0, first.StartSeconds);
        Assert.Equal(100, first.EndSeconds);
        Assert.True(second.StartSeconds >= first.EndSeconds);
        Assert.Equal(150, second.EndSeconds);
    }

    [Fact]
    public void Release_time_delays_the_first_step()
    {
        var ctx = Context(new SchedulingParameters(), new[] { Machine(1) },
            Released(1, 500, Step(10, 1, 100)));

        var op = new DispatchScheduler().Run(ctx, new[] { 0 }, FarDue(ctx)).Operations.Single();

        Assert.Equal(500, op.StartSeconds);
        Assert.Equal(600, op.EndSeconds);
    }
}
