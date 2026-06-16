namespace WorkPlanStudio.Scheduling.Tests;

public class EvaluatorTests
{
    [Fact]
    public void Rolls_up_lateness_makespan_and_utilisation()
    {
        var machines = new[] { Machine(1) };
        var ctx = Context(new SchedulingParameters(), machines,
            Job(1, Step(10, 1, 100)),
            Job(2, Step(10, 1, 200)));
        var due = new Dictionary<int, long> { [1] = 1000, [2] = 150 };

        // Order A then B: A 0–100 (target 1000, early); B 100–300 (target 150, late by 150).
        var schedule = new DispatchScheduler().Run(ctx, new[] { 0, 1 }, due);
        var eval = ScheduleEvaluator.Evaluate(schedule, ctx);

        Assert.Equal(300, eval.MakespanSeconds);
        Assert.Equal(150, eval.TotalTardinessSeconds);
        Assert.Equal(150, eval.MaxTardinessSeconds);
        Assert.Equal(1, eval.LateJobCount);
        Assert.Equal(2, eval.JobCount);
        Assert.Equal(0.5, eval.OnTimeRate, 6);
        Assert.Equal(200.0, eval.AverageFlowSeconds, 6);          // (100 + 300) / 2
        Assert.Equal(1.0, eval.UtilizationByWorkCenter[1], 6);    // (100 + 200) / (1 × 300)
        Assert.Equal(1.0, eval.AverageUtilization, 6);

        double expected = 1.0 * (300 / 3600.0) + 10.0 * (150 / 3600.0) + 100.0 * 1;
        Assert.Equal(expected, eval.Penalty, 6);
    }

    [Fact]
    public void An_early_job_has_zero_tardiness_but_negative_lateness()
    {
        var machines = new[] { Machine(1) };
        var ctx = Context(new SchedulingParameters(), machines, Job(1, Step(10, 1, 100)));
        var due = new Dictionary<int, long> { [1] = 1000 };

        var schedule = new DispatchScheduler().Run(ctx, new[] { 0 }, due);
        var job = schedule.Jobs.Single();

        Assert.Equal(-900, job.LatenessSeconds);
        Assert.Equal(0, job.TardinessSeconds);
        Assert.False(job.IsLate);
        Assert.Equal(1.0, ScheduleEvaluator.Evaluate(schedule, ctx).OnTimeRate, 6);
    }

    [Fact]
    public void Lower_tardiness_yields_a_lower_penalty()
    {
        var machines = new[] { Machine(1) };
        var ctx = Context(new SchedulingParameters(), machines,
            DueAt(1, 100_000, Step(10, 1, 200)),   // loose, long
            DueAt(2, 150, Step(10, 1, 100)));        // urgent, short
        var due = new Dictionary<int, long> { [1] = 100_000, [2] = 150 };

        var urgentFirst = new DispatchScheduler().Run(ctx, new[] { 1, 0 }, due);
        var looseFirst = new DispatchScheduler().Run(ctx, new[] { 0, 1 }, due);

        Assert.True(ScheduleEvaluator.Evaluate(urgentFirst, ctx).Penalty
                  < ScheduleEvaluator.Evaluate(looseFirst, ctx).Penalty);
    }
}
