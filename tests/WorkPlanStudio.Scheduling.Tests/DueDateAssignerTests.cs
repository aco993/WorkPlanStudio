namespace WorkPlanStudio.Scheduling.Tests;

public class DueDateAssignerTests
{
    private static ProductionJob JobWithSteps(long release, params long[] durations)
    {
        var steps = durations.Select((d, i) => Step((i + 1) * 10, 1, d)).ToArray();
        return Released(1, release, steps);
    }

    [Fact]
    public void Explicit_uses_the_job_value_when_present()
    {
        var job = DueAt(1, 5000, Step(10, 1, 100));
        var p = new SchedulingParameters { DueDateRule = DueDateRule.Explicit };
        Assert.Equal(5000, DueDateAssigner.DueFor(job, p));
    }

    [Fact]
    public void Explicit_falls_back_to_constant_allowance_when_absent()
    {
        var job = JobWithSteps(100, 100);
        var p = new SchedulingParameters { DueDateRule = DueDateRule.Explicit, ConstantAllowanceSeconds = 28800 };
        Assert.Equal(100 + 28800, DueDateAssigner.DueFor(job, p));
    }

    [Fact]
    public void TotalWorkContent_is_release_plus_factor_times_processing()
    {
        var job = JobWithSteps(100, 200, 400); // total 600
        var p = new SchedulingParameters { DueDateRule = DueDateRule.TotalWorkContent, TwkFlowFactor = 2.0 };
        Assert.Equal(100 + 1200, DueDateAssigner.DueFor(job, p));
    }

    [Fact]
    public void TotalWorkContent_uses_bankers_rounding()
    {
        var job = JobWithSteps(0, 5); // total 5; 0.5 × 5 = 2.5 → 2 (round half to even)
        var p = new SchedulingParameters { DueDateRule = DueDateRule.TotalWorkContent, TwkFlowFactor = 0.5 };
        Assert.Equal(2, DueDateAssigner.DueFor(job, p));
    }

    [Fact]
    public void NumberOfOperations_scales_with_the_step_count()
    {
        var job = JobWithSteps(100, 200, 400, 100); // 3 steps
        var p = new SchedulingParameters { DueDateRule = DueDateRule.NumberOfOperations, NopSecondsPerOp = 3600 };
        Assert.Equal(100 + 3 * 3600, DueDateAssigner.DueFor(job, p));
    }

    [Fact]
    public void EqualSlack_is_processing_plus_constant_slack()
    {
        var job = JobWithSteps(100, 200, 400); // total 600
        var p = new SchedulingParameters { DueDateRule = DueDateRule.EqualSlack, SlackSeconds = 7200 };
        Assert.Equal(100 + 600 + 7200, DueDateAssigner.DueFor(job, p));
    }

    [Fact]
    public void ConstantAllowance_is_release_plus_constant()
    {
        var job = JobWithSteps(100, 999);
        var p = new SchedulingParameters { DueDateRule = DueDateRule.ConstantAllowance, ConstantAllowanceSeconds = 28800 };
        Assert.Equal(100 + 28800, DueDateAssigner.DueFor(job, p));
    }

    [Fact]
    public void Assign_maps_every_job_by_id()
    {
        var ctx = Context(new SchedulingParameters { DueDateRule = DueDateRule.ConstantAllowance, ConstantAllowanceSeconds = 1000 },
            new[] { Machine(1) },
            Job(1, Step(10, 1, 100)), Released(2, 500, Step(10, 1, 100)));

        var due = DueDateAssigner.Assign(ctx);

        Assert.Equal(1000, due[1]);        // release 0 + 1000
        Assert.Equal(1500, due[2]);        // release 500 + 1000
    }
}
