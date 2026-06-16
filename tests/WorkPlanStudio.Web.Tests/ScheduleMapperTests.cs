namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Unit tests for the EF→domain boundary. Because <see cref="ScheduleMapper"/> is
/// pure, these run with hand-built entities — no database, no browser.
/// </summary>
public class ScheduleMapperTests
{
    private static WorkCenter Center(int id, bool active = true) =>
        new() { Id = id, Code = $"WC-{id}", Name = $"Center {id}", IsActive = active };

    private static Operation Op(int number, int workCenterId, decimal setup, decimal perPiece) =>
        new() { OperationNumber = number, WorkCenterId = workCenterId, Description = $"Op {number}", SetupTimeMinutes = setup, TimePerPieceMinutes = perPiece };

    private static WorkPlan Plan(int id, int lot, params Operation[] ops) =>
        new() { Id = id, PlanNumber = $"WP-{id}", PartName = $"Part {id}", Status = WorkPlanStatus.Released, LotSize = lot, Operations = ops.ToList() };

    private static SchedulingParameters RuleOnly =>
        new() { MultiStartRuns = 1, LocalSearchMaxSteps = 0 };

    // ----- ToSeconds (the only decimal → integer conversion in the whole app) -----

    [Fact]
    public void ToSeconds_computes_whole_lot_time()
    {
        // (35 + 4.2 × 100) min = 455 min = 27 300 s
        Assert.Equal(27_300, ScheduleMapper.ToSeconds(35m, 4.2m, 100));
    }

    [Theory]
    [InlineData(0.025, 2)]  // 1.5 s → 2 (round half to even)
    [InlineData(0.075, 4)]  // 4.5 s → 4 (round half to even)
    public void ToSeconds_uses_bankers_rounding(double perPieceMinutes, long expected)
    {
        Assert.Equal(expected, ScheduleMapper.ToSeconds(0m, (decimal)perPieceMinutes, 1));
    }

    // ----- BuildInput -----

    [Fact]
    public void BuildInput_maps_operations_to_steps_with_weight_from_lot_size()
    {
        var input = ScheduleMapper.BuildInput(
            new[] { Plan(1, 100, Op(10, 1, 10m, 0.8m), Op(20, 2, 35m, 4.2m)) },
            new[] { Center(1), Center(2) }, RuleOnly);

        Assert.NotNull(input);
        var job = Assert.Single(input!.Context.Jobs);
        Assert.Equal("WP-1", job.Reference);
        Assert.Equal(100, job.Weight);
        Assert.Equal(ScheduleMapper.ToSeconds(10m, 0.8m, 100), job.Steps[0].DurationSeconds);
    }

    [Fact]
    public void BuildInput_reindexes_steps_in_operation_order()
    {
        var input = ScheduleMapper.BuildInput(
            new[] { Plan(1, 1, Op(30, 1, 1m, 1m), Op(10, 1, 1m, 1m), Op(20, 1, 1m, 1m)) },
            new[] { Center(1) }, RuleOnly);

        // operations sorted 10,20,30 then re-indexed to strictly increasing 1,2,3
        Assert.Equal(new[] { 1, 2, 3 }, input!.Context.Jobs[0].Steps.Select(s => s.StepNumber).ToArray());
    }

    [Fact]
    public void BuildInput_drops_operations_on_inactive_work_centers()
    {
        var input = ScheduleMapper.BuildInput(
            new[] { Plan(1, 1, Op(10, 1, 1m, 1m), Op(20, 2, 1m, 1m)) },
            new[] { Center(1, active: true), Center(2, active: false) }, RuleOnly);

        var job = Assert.Single(input!.Context.Jobs);
        Assert.Single(job.Steps);                               // the op on the inactive WC is gone
        Assert.Equal(1, job.Steps[0].WorkCenterId);
        Assert.DoesNotContain(2, input.Context.Machines.Keys);  // inactive WC is not a machine
    }

    [Fact]
    public void BuildInput_skips_plans_left_without_schedulable_steps()
    {
        var input = ScheduleMapper.BuildInput(
            new[] { Plan(1, 1, Op(10, 99, 1m, 1m)) },   // only operation is on an unknown work center
            new[] { Center(1) }, RuleOnly);

        Assert.Null(input);
    }

    [Fact]
    public void BuildInput_returns_null_when_there_is_nothing_to_schedule()
    {
        Assert.Null(ScheduleMapper.BuildInput(Array.Empty<WorkPlan>(), new[] { Center(1) }, RuleOnly));
    }

    // ----- full pipeline: map → schedule → view -----

    [Fact]
    public void Full_pipeline_produces_gantt_rows_and_job_rows()
    {
        var input = ScheduleMapper.BuildInput(
            new[]
            {
                Plan(1, 100, Op(10, 1, 10m, 0.8m), Op(20, 2, 35m, 4.2m)),
                Plan(2, 50, Op(10, 1, 8m, 0.5m), Op(20, 2, 30m, 2.1m)),
            },
            new[] { Center(1), Center(2) }, RuleOnly);

        var result = new SchedulingEngine().Run(input!.Context);
        var view = ScheduleMapper.BuildView(result, input.Context, input.PlanById, 480);

        Assert.True(view.HasData);
        Assert.Equal(2, view.Jobs.Count);
        Assert.Equal(2, view.Rows.Count);                          // both work centers are used
        Assert.Equal(4, view.Rows.Sum(r => r.Bars.Count));         // 2 operations × 2 jobs
        Assert.All(view.Jobs, j => Assert.InRange(j.ColorIndex, 0, ScheduleMapper.PaletteSize - 1));
        // the Gantt "late" flag agrees with the table "late" flag
        Assert.Equal(view.Jobs.Any(j => j.IsLate), view.Rows.SelectMany(r => r.Bars).Any(b => b.IsLate));
    }

    [Fact]
    public void View_marks_late_jobs_and_bars_when_targets_are_impossible()
    {
        var tight = new SchedulingParameters
        {
            DueDateRule = DueDateRule.ConstantAllowance,
            ConstantAllowanceSeconds = 60,   // one minute — unreachable
            MultiStartRuns = 1,
            LocalSearchMaxSteps = 0
        };
        var input = ScheduleMapper.BuildInput(
            new[] { Plan(1, 100, Op(10, 1, 10m, 1m)) },
            new[] { Center(1) }, tight);

        var result = new SchedulingEngine().Run(input!.Context);
        var view = ScheduleMapper.BuildView(result, input.Context, input.PlanById, 480);

        Assert.True(view.Jobs.Single().IsLate);
        Assert.All(view.Rows.SelectMany(r => r.Bars), b => Assert.True(b.IsLate));
        Assert.Equal(1, view.Kpis.LateJobCount);
    }
}
