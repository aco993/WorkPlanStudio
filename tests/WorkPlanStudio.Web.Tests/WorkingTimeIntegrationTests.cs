using WorkPlanStudio.Validation;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The working-time model at the application boundary: the mapper builds a
/// calendar per work center from the plant settings, the scheduler honours it,
/// the Gantt view carries the closed time, and the settings and absences
/// survive a round trip through real SQLite.
/// </summary>
public class WorkingTimeIntegrationTests
{
    private const long Hour = 3600;

    // Monday 1 June 2026, 06:00. Thursday 4 June is Fronleichnam in NW.
    private static readonly DateTime Horizon = new(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc);

    private static WorkCenter Center(int id, string pattern) =>
        new() { Id = id, Code = $"WC-{id}", Name = $"Center {id}", IsActive = true, ShiftPatternKey = pattern };

    private static WorkPlan Plan(int id, params Operation[] ops) =>
        new() { Id = id, PlanNumber = $"WP-{id}", PartName = $"Part {id}", Status = WorkPlanStatus.Released, LotSize = 1, Operations = ops.ToList() };

    private static Operation Op(int number, int workCenterId, decimal minutes) =>
        new() { OperationNumber = number, WorkCenterId = workCenterId, Description = $"Op {number}", SetupTimeMinutes = minutes, TimePerPieceMinutes = 0 };

    private static ProductionOrder Released(WorkPlan plan, int dueDays = 10) => new()
    {
        Id = plan.Id,
        OrderNumber = $"PO-{plan.Id}",
        WorkPlanId = plan.Id,
        Quantity = 1,
        ReleaseUtc = Horizon,
        DueUtc = Horizon.AddDays(dueDays),
        Status = ProductionOrderStatus.Released,
        RoutingSnapshotJson = RoutingSnapshot.Capture(plan).Serialize()
    };

    private static SchedulingParameters RuleOnly => new() { DispatchRule = DispatchRule.Fifo, MultiStartRuns = 1, LocalSearchMaxSteps = 0 };

    private static ShopCalendar Calendar(PlantSettings? settings = null, params WorkCenter[] centers) =>
        ShopCalendar.From(settings ?? new PlantSettings(), centers, []);

    // ----- mapper -----

    [Fact]
    public void A_one_shift_center_starts_work_at_seven_and_pauses_across_the_break()
    {
        var center = Center(1, ShiftPatterns.OneShift.Key);
        var preparation = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, Op(10, 1, 6 * 60)))], [center], RuleOnly, Calendar(null, center));

        var result = new SchedulingEngine().Run(preparation.Input!.Context);
        var op = Assert.Single(result.Schedule.Operations);

        Assert.Equal(Hour, op.StartSeconds);                    // 07:00, one hour after the 06:00 horizon
        Assert.Equal(30 * 60, op.PausedSeconds);                // the 11:00 break
        Assert.Equal(6 * Hour + 30 * 60, op.DurationSeconds);
        Assert.Equal(Horizon, preparation.Input.Horizon);
        Assert.True(preparation.Input.TimelineByWorkCenter.ContainsKey(1));
    }

    [Fact]
    public void A_continuous_center_is_unconstrained()
    {
        var center = Center(1, ShiftPatterns.Continuous.Key);
        var preparation = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, Op(10, 1, 20 * 60)))], [center], RuleOnly, Calendar(null, center));

        var op = Assert.Single(new SchedulingEngine().Run(preparation.Input!.Context).Schedule.Operations);
        Assert.Equal(0, op.StartSeconds);
        Assert.Equal(20 * Hour, op.EndSeconds);
    }

    [Fact]
    public void Without_a_calendar_the_mapper_behaves_as_before()
    {
        var center = Center(1, ShiftPatterns.OneShift.Key);
        var preparation = ScheduleMapper.BuildInputFromOrders([Released(Plan(1, Op(10, 1, 20 * 60)))], [center], RuleOnly);

        var machine = preparation.Input!.Context.Machines[1];
        Assert.Empty(machine.AvailabilityWindows);
        Assert.Empty(machine.Blackouts);
    }

    [Fact]
    public void An_operation_longer_than_the_longest_shift_rejects_the_order_with_a_diagnostic()
    {
        var center = Center(1, ShiftPatterns.OneShift.Key);   // 8 h is the most one shift can hold
        var preparation = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, Op(10, 1, 9 * 60))), Released(Plan(2, Op(10, 1, 60)))], [center], RuleOnly, Calendar(null, center));

        var error = Assert.Single(preparation.Errors);
        Assert.Equal(SchedulePreparationErrorCode.OperationExceedsShiftWindow, error.Code);
        Assert.Equal("PO-1", error.OrderReference);
        Assert.Equal("WC-1", error.WorkCenterReference);
        Assert.Single(preparation.Input!.Context.Jobs);        // the other order still schedules
    }

    [Fact]
    public void A_state_holiday_and_the_weekend_close_the_center()
    {
        // Two 8-hour jobs on a one-shift NW machine from Monday: Mon, Tue, Wed
        // hold one each... the third lands on Friday because Thursday is
        // Fronleichnam; a fourth waits for Monday.
        var center = Center(1, ShiftPatterns.OneShift.Key);
        var orders = Enumerable.Range(1, 4).Select(i => Released(Plan(i, Op(10, 1, 8 * 60)))).ToList();
        var preparation = ScheduleMapper.BuildInputFromOrders(orders, [center], RuleOnly, Calendar(null, center));

        var schedule = new SchedulingEngine().Run(preparation.Input!.Context).Schedule;
        var starts = schedule.Operations.OrderBy(o => o.StartSeconds).Select(o => Horizon.AddSeconds(o.StartSeconds)).ToList();

        Assert.Equal(new DateTime(2026, 6, 1, 7, 0, 0), starts[0]);
        Assert.Equal(new DateTime(2026, 6, 2, 7, 0, 0), starts[1]);
        Assert.Equal(new DateTime(2026, 6, 3, 7, 0, 0), starts[2]);
        Assert.Equal(new DateTime(2026, 6, 5, 7, 0, 0), starts[3]);   // Friday, not the holiday
    }

    [Fact]
    public void A_state_without_the_holiday_uses_thursday()
    {
        var center = Center(1, ShiftPatterns.OneShift.Key);
        var settings = new PlantSettings { State = "BE" };
        var orders = Enumerable.Range(1, 4).Select(i => Released(Plan(i, Op(10, 1, 8 * 60)))).ToList();
        var preparation = ScheduleMapper.BuildInputFromOrders(orders, [center], RuleOnly, Calendar(settings, center));

        var schedule = new SchedulingEngine().Run(preparation.Input!.Context).Schedule;
        var last = schedule.Operations.Max(o => o.StartSeconds);

        Assert.Equal(new DateTime(2026, 6, 4, 7, 0, 0), Horizon.AddSeconds(last));
    }

    [Fact]
    public void The_view_carries_horizon_closed_time_and_pauses()
    {
        var center = Center(1, ShiftPatterns.OneShift.Key);
        var preparation = ScheduleMapper.BuildInputFromOrders(
            [Released(Plan(1, Op(10, 1, 6 * 60)))], [center], RuleOnly, Calendar(null, center));
        var input = preparation.Input!;
        var result = new SchedulingEngine().Run(input.Context);

        var view = ScheduleMapper.BuildView(result, input.Context, input.OriginById, 480, input.Horizon, input.TimelineByWorkCenter);

        Assert.Equal(Horizon, view.Horizon);
        Assert.Equal(30 * 60, view.TotalPausedSeconds);
        var row = Assert.Single(view.Rows);
        var bar = Assert.Single(row.Bars);
        Assert.Equal(30 * 60, bar.PausedSeconds);
        Assert.Contains(row.Closed, c => c.Kind == SegmentKind.OffShift && c.StartSeconds == 0 && c.EndSeconds == Hour);
        Assert.Contains(row.Closed, c => c.Kind == SegmentKind.Break && c.StartSeconds == 5 * Hour);
        Assert.All(row.Closed, c => Assert.True(c.EndSeconds <= view.MakespanSeconds));
    }

    // ----- persistence -----

    [Fact]
    public async Task Plant_settings_round_trip_through_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var storage = new FakeStorage();
        var database = files.CreateDatabase("settings.db", storage);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var service = new PlantSettingsService(database);
        var seeded = await service.GetAsync(cancellationToken);
        Assert.Equal("NW", seeded.State);   // the seed row

        var saved = await service.SaveAsync(new PlantSettings { State = "by", SundayWorkAllowed = true, SundayBoundaryShiftHours = 6, MinimumRestHours = 10 }, cancellationToken);
        Assert.True(saved.IsSuccess);

        var reloaded = await new PlantSettingsService(files.CreateDatabase("settings-2.db", storage)).GetAsync(cancellationToken);
        Assert.Equal("BY", reloaded.State);
        Assert.True(reloaded.SundayWorkAllowed);
        Assert.Equal(GermanState.BY, reloaded.ToRules().State);
        Assert.Equal(TimeSpan.FromHours(6), reloaded.ToRules().SundayBoundaryShift);
        Assert.Equal(TimeSpan.FromHours(10), reloaded.ToRules().MinimumRest);
    }

    [Fact]
    public async Task Invalid_plant_settings_are_rejected_before_they_reach_the_database()
    {
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("settings.db", new FakeStorage());
        var service = new PlantSettingsService(database);

        var result = await service.SaveAsync(new PlantSettings { State = "XX", SundayBoundaryShiftHours = 9 }, TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationResultStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ValidationIssues!, i => i.Field == nameof(PlantSettings.State));
        Assert.Contains(result.ValidationIssues!, i => i.Field == nameof(PlantSettings.SundayBoundaryShiftHours));
    }

    [Fact]
    public async Task Absences_are_added_validated_listed_and_removed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("absences.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var service = new WorkCenterService(database);
        var center = (await service.GetAllAsync(cancellationToken)).First();

        var seeded = await service.GetAbsencesAsync(cancellationToken);
        var seededAbsence = Assert.Single(seeded);
        Assert.Equal("Spindle service", seededAbsence.Label);
        Assert.NotNull(seededAbsence.WorkCenter);

        var inverted = await service.AddAbsenceAsync(new WorkCenterAbsence
        {
            WorkCenterId = center.Id,
            Start = new DateTime(2026, 7, 2),
            End = new DateTime(2026, 7, 1)
        }, cancellationToken);
        Assert.Equal(ApplicationResultStatus.ValidationFailed, inverted.Status);

        var added = await service.AddAbsenceAsync(new WorkCenterAbsence
        {
            WorkCenterId = center.Id,
            Start = new DateTime(2026, 7, 1),
            End = new DateTime(2026, 7, 3),
            Kind = AbsenceKind.Vacation,
            Label = " Summer break "
        }, cancellationToken);
        Assert.True(added.IsSuccess);

        var all = await service.GetAbsencesAsync(cancellationToken);
        Assert.Equal(2, all.Count);
        var summer = all.Single(a => a.Kind == AbsenceKind.Vacation);
        Assert.Equal("Summer break", summer.Label);

        Assert.True((await service.RemoveAbsenceAsync(summer.Id, cancellationToken)).IsSuccess);
        Assert.Equal(ApplicationResultStatus.NotFound, (await service.RemoveAbsenceAsync(summer.Id, cancellationToken)).Status);
        Assert.Single(await service.GetAbsencesAsync(cancellationToken));
    }

    [Fact]
    public async Task A_work_center_must_use_a_known_shift_pattern()
    {
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("shift.db", new FakeStorage());
        var service = new WorkCenterService(database);

        var result = await service.SaveAsync(
            new WorkCenter { Code = "NEW-1", Name = "New", ShiftPatternKey = "four-shift" }, TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationResultStatus.ValidationFailed, result.Status);
        Assert.Contains(result.ValidationIssues!, i => i.MessageKey == "Val_ShiftPatternUnknown");
        Assert.Empty(WorkCenterValidator.Validate(new WorkCenter { Code = "OK", Name = "Ok", ShiftPatternKey = ShiftPatterns.TwoShift.Key }));
    }

    [Fact]
    public async Task The_seed_gives_every_work_center_a_pattern_and_schedules_without_rejections()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("seed.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var centers = new WorkCenterService(database);
        Assert.All(await centers.GetAllAsync(cancellationToken), c => Assert.NotNull(ShiftPatterns.ByKey(c.ShiftPatternKey)));

        var scheduler = new ProductionScheduleService(new ProductionOrderService(database), centers, new PlantSettingsService(database));
        var result = await scheduler.GenerateAsync(new SchedulingParameters { MultiStartRuns = 1, LocalSearchMaxSteps = 0 }, cancellationToken);

        Assert.True(result.HasData);
        Assert.Empty(result.PreparationErrors);
        Assert.Equal(7, result.Kpis.JobCount);
        Assert.NotNull(result.Horizon);
        Assert.Contains(result.Rows, r => r.Closed.Any(c => c.Kind == SegmentKind.Holiday && c.Label == "CorpusChristi"));
        Assert.Contains(result.Rows, r => r.Closed.Any(c => c.Kind == SegmentKind.Absence));
        Assert.True(result.TotalPausedSeconds > 0);
    }
}
