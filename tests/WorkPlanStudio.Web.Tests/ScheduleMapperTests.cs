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

    private static readonly DateTime Horizon = new(2026, 6, 15, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>A released order over a plan's routing — what the scheduler actually consumes.</summary>
    private static ProductionOrder Released(WorkPlan plan, int quantity, int priority = 1, int dueHours = 240) =>
        new()
        {
            Id = plan.Id,
            OrderNumber = $"PO-{plan.Id}",
            WorkPlanId = plan.Id,
            Quantity = quantity,
            Priority = priority,
            ReleaseUtc = Horizon,
            DueUtc = Horizon.AddHours(dueHours),
            Status = ProductionOrderStatus.Released,
            RoutingSnapshotJson = RoutingSnapshot.Capture(plan).Serialize()
        };

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

    // ----- BuildInputFromOrders -----

    [Fact]
    public void Steps_come_from_the_snapshot_and_scale_with_the_order_quantity()
    {
        var input = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, 100, Op(10, 1, 10m, 0.8m), Op(20, 2, 35m, 4.2m)), quantity: 100)],
            [Center(1), Center(2)], RuleOnly);

        Assert.NotNull(input.Input);
        Assert.Empty(input.Errors);
        var job = Assert.Single(input.Input!.Context.Jobs);
        Assert.Equal("PO-1", job.Reference);                                        // the order, not the plan
        Assert.Equal(ScheduleMapper.ToSeconds(10m, 0.8m, 100), job.Steps[0].DurationSeconds);
    }

    [Fact]
    public void The_dispatch_weight_is_the_order_priority_not_its_quantity()
    {
        var input = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, 1, Op(10, 1, 1m, 1m)), quantity: 5000, priority: 4)],
            [Center(1)], RuleOnly);

        Assert.Equal(4, Assert.Single(input.Input!.Context.Jobs).Weight);
    }

    [Fact]
    public void Release_and_due_dates_become_offsets_from_the_earliest_release()
    {
        var early = Released(Plan(1, 1, Op(10, 1, 1m, 1m)), quantity: 1, dueHours: 48);
        var late = Released(Plan(2, 1, Op(10, 1, 1m, 1m)), quantity: 1, dueHours: 72);
        late.ReleaseUtc = Horizon.AddHours(24);

        var input = ScheduleMapper.BuildInputFromOrders([early, late], [Center(1)], RuleOnly);

        var first = input.Input!.Context.Jobs.Single(j => j.Id == 1);
        var second = input.Input.Context.Jobs.Single(j => j.Id == 2);

        Assert.Equal(0, first.ReleaseSeconds);                       // the horizon is the earliest release
        Assert.Equal(24 * 3600, second.ReleaseSeconds);
        Assert.Equal(48 * 3600, first.ExplicitDueSeconds);
        Assert.Equal(72 * 3600, second.ExplicitDueSeconds);
    }

    [Fact]
    public void Snapshot_operations_are_reindexed_in_operation_order()
    {
        var input = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, 1, Op(30, 1, 1m, 1m), Op(10, 1, 1m, 1m), Op(20, 1, 1m, 1m)), quantity: 1)],
            [Center(1)], RuleOnly);

        Assert.Equal(new[] { 1, 2, 3 }, input.Input!.Context.Jobs[0].Steps.Select(s => s.StepNumber).ToArray());
    }

    [Fact]
    public void An_order_is_rejected_whole_when_a_work_center_is_inactive()
    {
        var input = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, 1, Op(10, 1, 1m, 1m), Op(20, 2, 1m, 1m)), quantity: 1)],
            [Center(1, active: true), Center(2, active: false)], RuleOnly);

        Assert.Null(input.Input);
        var error = Assert.Single(input.Errors);
        Assert.Equal(SchedulePreparationErrorCode.InactiveWorkCenter, error.Code);
        Assert.Equal(20, error.OperationNumber);
        Assert.Equal("WC-2", error.WorkCenterReference);
    }

    [Fact]
    public void An_order_is_rejected_when_a_work_center_no_longer_exists()
    {
        var input = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, 1, Op(10, 99, 1m, 1m)), quantity: 1)],
            [Center(1)], RuleOnly);

        Assert.Null(input.Input);
        var error = Assert.Single(input.Errors);
        Assert.Equal(SchedulePreparationErrorCode.MissingWorkCenter, error.Code);
        Assert.Equal(10, error.OperationNumber);
    }

    [Fact]
    public void Nothing_to_schedule_yields_no_input_and_no_errors()
    {
        var preparation = ScheduleMapper.BuildInputFromOrders([], [Center(1)], RuleOnly);

        Assert.Null(preparation.Input);
        Assert.Empty(preparation.Errors);
    }

    [Fact]
    public void Invalid_orders_are_reported_while_valid_ones_stay_complete()
    {
        var preparation = ScheduleMapper.BuildInputFromOrders(
            [
                Released(Plan(1, 1, Op(10, 1, 1m, 1m), Op(20, 2, 1m, 1m)), quantity: 1),
                Released(Plan(2, 1, Op(10, 99, 1m, 1m)), quantity: 1),
                Released(Plan(3, 1, Op(10, 1, 1m, 1m), Op(20, 1, 1m, 1m)), quantity: 1)
            ],
            [Center(1), Center(2, active: false)],
            RuleOnly);

        Assert.Equal(new[] { 1, 2 }, preparation.RejectedOrderIds.Order().ToArray());
        var validJob = Assert.Single(preparation.Input!.Context.Jobs);
        Assert.Equal(3, validJob.Id);
        Assert.Equal(2, validJob.Steps.Count);
    }

    [Fact]
    public void Work_center_capacity_reaches_the_engine()
    {
        var center = Center(1);
        center.ParallelCapacity = 4;

        var preparation = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, 1, Op(10, 1, 1m, 1m)), quantity: 1)],
            [center],
            RuleOnly);

        Assert.Equal(4, preparation.Input!.Context.Machines[1].ParallelCapacity);
        Assert.Empty(preparation.Errors);
    }

    // ----- full pipeline: map → schedule → view -----

    [Fact]
    public void Full_pipeline_produces_gantt_rows_and_job_rows()
    {
        var input = ScheduleMapper.BuildInputFromOrders(
            [
                Released(Plan(1, 100, Op(10, 1, 10m, 0.8m), Op(20, 2, 35m, 4.2m)), quantity: 100),
                Released(Plan(2, 50, Op(10, 1, 8m, 0.5m), Op(20, 2, 30m, 2.1m)), quantity: 50),
            ],
            [Center(1), Center(2)], RuleOnly);

        var result = new SchedulingEngine().Run(input.Input!.Context);
        var view = ScheduleMapper.BuildView(result, input.Input.Context, input.Input.OriginById, 480);

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
        var input = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, 100, Op(10, 1, 10m, 1m)), quantity: 100)],
            [Center(1)], tight);

        var result = new SchedulingEngine().Run(input.Input!.Context);
        var view = ScheduleMapper.BuildView(result, input.Input.Context, input.Input.OriginById, 480);

        Assert.True(view.Jobs.Single().IsLate);
        Assert.All(view.Rows.SelectMany(r => r.Bars), b => Assert.True(b.IsLate));
        Assert.Equal(1, view.Kpis.LateJobCount);
    }
}
