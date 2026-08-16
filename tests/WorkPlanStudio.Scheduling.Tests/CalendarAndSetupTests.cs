namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// Availability calendars and sequence-dependent change-over. Both default to
/// "no constraint", so these also pin that adding the fields did not change the
/// behaviour of instances that do not use them.
/// </summary>
public class CalendarAndSetupTests
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;

    /// <summary>A work center available 08:00–16:00 every 24 hours.</summary>
    private static MachineCapacity DayShift(int id, int capacity = 1) =>
        Machine(id, capacity) with
        {
            AvailabilityWindows = [new CapacityWindow(8 * Hour, 16 * Hour)],
            CalendarPeriodSeconds = Day
        };

    // ----- calendars -----

    [Fact]
    public void Work_starts_at_the_first_open_window_rather_than_at_the_horizon()
    {
        var context = Context(RuleOnly(DispatchRule.Fifo), [DayShift(1)], Job(1, Step(10, 1, Hour)));

        var op = new DispatchScheduler().Run(context, [0], DueDateAssigner.Assign(context)).Operations.Single();

        Assert.Equal(8 * Hour, op.StartSeconds);
        Assert.Equal(9 * Hour, op.EndSeconds);
    }

    [Fact]
    public void Work_that_does_not_fit_the_remaining_window_waits_for_the_next_day()
    {
        // Two 5-hour operations, an 8-hour shift: the second cannot fit after the
        // first, so it moves to the next day rather than running into the night.
        var context = Context(RuleOnly(DispatchRule.Fifo), [DayShift(1)],
            Job(1, Step(10, 1, 5 * Hour)),
            Job(2, Step(10, 1, 5 * Hour)));

        var schedule = new DispatchScheduler().Run(context, [0, 1], DueDateAssigner.Assign(context));
        var first = schedule.Operations.Single(o => o.JobId == 1);
        var second = schedule.Operations.Single(o => o.JobId == 2);

        Assert.Equal(8 * Hour, first.StartSeconds);
        Assert.Equal(13 * Hour, first.EndSeconds);
        Assert.Equal(Day + 8 * Hour, second.StartSeconds);   // next period, not 13:00
    }

    [Fact]
    public void The_calendar_repeats_indefinitely()
    {
        // Five 8-hour jobs on a one-shift machine must land on five consecutive days.
        var jobs = Enumerable.Range(1, 5).Select(i => Job(i, Step(10, 1, 8 * Hour))).ToArray();
        var context = Context(RuleOnly(DispatchRule.Fifo), [DayShift(1)], jobs);

        var schedule = new DispatchScheduler().Run(context, [0, 1, 2, 3, 4], DueDateAssigner.Assign(context));

        var starts = schedule.Operations.OrderBy(o => o.StartSeconds).Select(o => o.StartSeconds).ToArray();
        Assert.Equal(Enumerable.Range(0, 5).Select(d => d * Day + 8 * Hour).ToArray(), starts);
    }

    [Fact]
    public void A_step_that_cannot_fit_any_window_is_rejected_when_the_context_is_built()
    {
        // Rejected at construction, not mid-search: the search evaluates thousands
        // of orders and must not throw from inside that loop.
        var ex = Assert.Throws<ArgumentException>(() =>
            Context(RuleOnly(DispatchRule.Fifo), [DayShift(1)], Job(1, Step(10, 1, 9 * Hour))));

        Assert.Contains("longest availability window", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_must_lie_inside_the_declared_period()
    {
        var broken = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(0, 2 * Day)],
            CalendarPeriodSeconds = Day
        };

        Assert.Throws<ArgumentException>(() =>
            Context(RuleOnly(DispatchRule.Fifo), [broken], Job(1, Step(10, 1, Hour))));
    }

    [Fact]
    public void Windows_without_a_period_are_rejected()
    {
        var broken = Machine(1) with { AvailabilityWindows = [new CapacityWindow(0, Hour)] };

        Assert.Throws<ArgumentException>(() =>
            Context(RuleOnly(DispatchRule.Fifo), [broken], Job(1, Step(10, 1, Hour))));
    }

    [Fact]
    public void Overlapping_windows_are_rejected()
    {
        var broken = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(0, 4 * Hour), new CapacityWindow(2 * Hour, 6 * Hour)],
            CalendarPeriodSeconds = Day
        };

        Assert.Throws<ArgumentException>(() =>
            Context(RuleOnly(DispatchRule.Fifo), [broken], Job(1, Step(10, 1, Hour))));
    }

    // ----- sequence-dependent setup -----

    private static MachineCapacity WithChangeover(int id, long seconds, int capacity = 1) =>
        Machine(id, capacity) with
        {
            SetupDurations =
            [
                new SetupDuration("STEEL", "ALU", seconds),
                new SetupDuration("ALU", "STEEL", seconds)
            ]
        };

    [Fact]
    public void Running_the_same_family_twice_costs_no_change_over()
    {
        var context = Context(RuleOnly(DispatchRule.Fifo), [WithChangeover(1, Hour)],
            Job(1, new JobStep(10, 1, Hour, "STEEL")),
            Job(2, new JobStep(10, 1, Hour, "STEEL")));

        var schedule = new DispatchScheduler().Run(context, [0, 1], DueDateAssigner.Assign(context));

        Assert.All(schedule.Operations, o => Assert.Equal(0, o.SetupSeconds));
        Assert.Equal(2 * Hour, schedule.MakespanSeconds);
    }

    [Fact]
    public void Switching_family_costs_the_declared_change_over()
    {
        var context = Context(RuleOnly(DispatchRule.Fifo), [WithChangeover(1, Hour)],
            Job(1, new JobStep(10, 1, Hour, "STEEL")),
            Job(2, new JobStep(10, 1, Hour, "ALU")));

        var schedule = new DispatchScheduler().Run(context, [0, 1], DueDateAssigner.Assign(context));
        var second = schedule.Operations.Single(o => o.JobId == 2);

        Assert.Equal(Hour, second.SetupSeconds);
        Assert.Equal(Hour, second.ProcessingSeconds);
        Assert.Equal(3 * Hour, schedule.MakespanSeconds);   // 1h + (1h setup + 1h)
    }

    [Fact]
    public void The_first_operation_on_a_slot_never_pays_setup()
    {
        var context = Context(RuleOnly(DispatchRule.Fifo), [WithChangeover(1, Hour)],
            Job(1, new JobStep(10, 1, Hour, "ALU")));

        Assert.Equal(0, new DispatchScheduler()
            .Run(context, [0], DueDateAssigner.Assign(context)).Operations.Single().SetupSeconds);
    }

    [Fact]
    public void An_undeclared_transition_is_free()
    {
        var context = Context(RuleOnly(DispatchRule.Fifo), [WithChangeover(1, Hour)],
            Job(1, new JobStep(10, 1, Hour, "STEEL")),
            Job(2, new JobStep(10, 1, Hour, "BRASS")));   // STEEL -> BRASS is not in the matrix

        var schedule = new DispatchScheduler().Run(context, [0, 1], DueDateAssigner.Assign(context));

        Assert.Equal(0, schedule.Operations.Single(o => o.JobId == 2).SetupSeconds);
    }

    /// <summary>
    /// The reason change-over is interesting: it makes the sequence matter beyond
    /// queueing, so grouping the same family together beats alternating.
    /// </summary>
    [Fact]
    public void Grouping_families_beats_alternating_them()
    {
        var machines = new[] { WithChangeover(1, 2 * Hour) };
        var jobs = new[]
        {
            Job(1, new JobStep(10, 1, Hour, "STEEL")),
            Job(2, new JobStep(10, 1, Hour, "ALU")),
            Job(3, new JobStep(10, 1, Hour, "STEEL")),
            Job(4, new JobStep(10, 1, Hour, "ALU")),
        };
        var context = Context(RuleOnly(DispatchRule.Fifo), machines, jobs);
        var due = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();

        long grouped = scheduler.Run(context, [0, 2, 1, 3], due).MakespanSeconds;    // SS AA -> one change-over
        long alternating = scheduler.Run(context, [0, 1, 2, 3], due).MakespanSeconds; // SASA -> three

        Assert.Equal(4 * Hour + 2 * Hour, grouped);
        Assert.Equal(4 * Hour + 6 * Hour, alternating);
        Assert.True(grouped < alternating);
    }

    [Fact]
    public void A_slot_that_avoids_change_over_wins_over_one_that_is_free_sooner()
    {
        // Two slots. Slot 0 is busy but already on ALU; slot 1 is free but on
        // STEEL and would need a long change-over. Earliest *finish* should win.
        var machine = Machine(1, capacity: 2) with
        {
            SetupDurations = [new SetupDuration("STEEL", "ALU", 10 * Hour)]
        };
        var context = Context(RuleOnly(DispatchRule.Fifo), [machine],
            Job(1, new JobStep(10, 1, 3 * Hour, "ALU")),     // -> slot 0
            Job(2, new JobStep(10, 1, Hour, "STEEL")),       // -> slot 1
            Job(3, new JobStep(10, 1, Hour, "ALU")));        // should follow job 1 on slot 0

        var schedule = new DispatchScheduler().Run(context, [0, 1, 2], DueDateAssigner.Assign(context));
        var third = schedule.Operations.Single(o => o.JobId == 3);

        Assert.Equal(0, third.SetupSeconds);
        Assert.Equal(3 * Hour, third.StartSeconds);
    }

    // ----- defaults unchanged -----

    [Fact]
    public void An_instance_without_calendars_or_setups_behaves_exactly_as_before()
    {
        var context = Context(RuleOnly(DispatchRule.Fifo), [Machine(1)],
            Job(1, Step(10, 1, 100)), Job(2, Step(10, 1, 100)));

        var schedule = new DispatchScheduler().Run(context, [0, 1], DueDateAssigner.Assign(context));

        Assert.Equal(200, schedule.MakespanSeconds);
        Assert.All(schedule.Operations, o => Assert.Equal(0, o.SetupSeconds));
        Feasibility.AssertFeasible(schedule, context);
    }

    [Fact]
    public void Calendars_and_setups_stay_deterministic()
    {
        SchedulingContext Build() => Context(
            new SchedulingParameters { DispatchRule = DispatchRule.EarliestDueDate, Seed = 4242 },
            [DayShift(1) with { SetupDurations = [new SetupDuration("A", "B", Hour)] }, DayShift(2, capacity: 2)],
            Job(1, new JobStep(10, 1, 2 * Hour, "A"), new JobStep(20, 2, Hour, "B")),
            Job(2, new JobStep(10, 1, 3 * Hour, "B"), new JobStep(20, 2, 2 * Hour, "A")),
            Job(3, new JobStep(10, 2, Hour, "A"), new JobStep(20, 1, 2 * Hour, "B")));

        Assert.Equal(
            new SchedulingEngine().Run(Build()).Schedule.Signature(),
            new SchedulingEngine().Run(Build()).Schedule.Signature());
    }
}
