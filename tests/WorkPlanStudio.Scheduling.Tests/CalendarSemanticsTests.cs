namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The calendar decisions that are easy to get subtly wrong: what "the longest
/// placement" means when every gap is bridgeable, which change-overs a work
/// center can actually be charged, where a zero-length step goes, and what a
/// blackout is taken to mean. Each of these rejected or delayed work that the
/// dispatcher demonstrably places.
/// </summary>
public class CalendarSemanticsTests
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;

    // ----- a fully bridgeable calendar is unbounded, not long -----

    /// <summary>
    /// 82 800 open seconds a day — 95.8 % — with the single one-hour gap and the
    /// zero-length period wrap both bridgeable. The old bound walked two laps of
    /// the window list and reported 165 600 s (46 h), so a 55-hour operation was
    /// rejected at construction while a 46-hour one placed fine and paused across
    /// exactly the gaps the rejected one needed.
    /// </summary>
    private static MachineCapacity AlmostAlwaysOpen() => Machine(1) with
    {
        AvailabilityWindows = [new CapacityWindow(0, 8 * Hour), new CapacityWindow(9 * Hour, Day)],
        CalendarPeriodSeconds = Day,
        MaxBridgeableGapSeconds = Hour
    };

    [Fact]
    public void A_calendar_whose_every_gap_is_bridgeable_places_an_operation_of_any_length()
    {
        Assert.Equal(long.MaxValue, AlmostAlwaysOpen().LongestPlacementSeconds);

        var context = Context(RuleOnly(DispatchRule.Fifo), [AlmostAlwaysOpen()], Job(1, Step(10, 1, 200_000)));
        var op = new DispatchScheduler().Run(context, [0], DueDateAssigner.Assign(context), Ct).Operations.Single();

        Assert.Equal(0, op.StartSeconds);
        Assert.Equal(200_000, op.ProcessingSeconds);
        Assert.Equal(3 * Hour, op.PausedSeconds);   // three of the one-hour gaps bridged
        Feasibility.AssertFeasible(new Schedule([op], []), context);
    }

    [Fact]
    public void A_calendar_with_one_unbridgeable_gap_is_still_bounded()
    {
        var closedAtNight = AlmostAlwaysOpen() with { MaxBridgeableGapSeconds = 0 };

        // The 15-hour window runs straight into the next period's 8-hour one -
        // that wrap is a zero-length gap - but the 09:00 gap is not bridgeable,
        // so the run stops at 23 hours instead of going round for ever.
        Assert.Equal(23 * Hour, closedAtNight.LongestPlacementSeconds);
        Assert.Throws<ArgumentException>(() =>
            Context(RuleOnly(DispatchRule.Fifo), [closedAtNight], Job(1, Step(10, 1, 24 * Hour))));
    }

    [Fact]
    public void A_run_that_wraps_the_period_boundary_is_measured_whole()
    {
        // Open 22:00-02:00 across midnight, written as two windows with the wrap
        // bridgeable and the daytime gap not.
        var nightShift = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(0, 2 * Hour), new CapacityWindow(22 * Hour, Day)],
            CalendarPeriodSeconds = Day,
            MaxBridgeableGapSeconds = 0
        };

        Assert.Equal(4 * Hour, nightShift.LongestPlacementSeconds);
    }

    // ----- worst change-over is over the families that actually run here -----

    /// <summary>
    /// An 8-hour window, a single 4-hour ALU job, and a matrix entry for a
    /// transition from a family no job in this context uses. The charged setup for
    /// that job is always 0 — it is the first operation on the slot and no other
    /// family appears — but the feasibility check used to take the maximum over
    /// the whole declared matrix and reject it.
    /// </summary>
    [Fact]
    public void A_change_over_from_a_family_no_job_uses_does_not_reject_the_job()
    {
        var machine = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(0, 8 * Hour)],
            CalendarPeriodSeconds = Day,
            SetupDurations = [new SetupDuration("NEVER_USED", "ALU", 8 * Hour)]
        };
        var context = Context(RuleOnly(DispatchRule.Fifo), [machine], Job(1, new JobStep(10, 1, 4 * Hour, "ALU")));

        var op = new DispatchScheduler().Run(context, [0], DueDateAssigner.Assign(context), Ct).Operations.Single();

        Assert.Equal(0, op.SetupSeconds);
        Assert.Equal(4 * Hour, op.ProcessingSeconds);
    }

    /// <summary>
    /// The bound is not simply dropped: a change-over between two families that
    /// both run on this center still has to fit the window with its job.
    /// </summary>
    [Fact]
    public void A_change_over_between_families_that_do_run_here_is_still_charged_against_the_window()
    {
        var machine = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(0, 8 * Hour)],
            CalendarPeriodSeconds = Day,
            SetupDurations = [new SetupDuration("STEEL", "ALU", 5 * Hour)]
        };

        var error = Assert.Throws<ArgumentException>(() => Context(
            RuleOnly(DispatchRule.Fifo),
            [machine],
            Job(1, new JobStep(10, 1, Hour, "STEEL")),
            Job(2, new JobStep(10, 1, 4 * Hour, "ALU"))));

        Assert.Contains("including change-over", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_over_survives_a_calendar_window_boundary_on_the_same_slot()
    {
        // Two ALU jobs on a day shift: the second runs tomorrow on the same slot,
        // and the slot still remembers the family, so no change-over is charged.
        var machine = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(8 * Hour, 16 * Hour)],
            CalendarPeriodSeconds = Day,
            SetupDurations = [new SetupDuration("STEEL", "ALU", Hour), new SetupDuration("ALU", "STEEL", Hour)]
        };
        var context = Context(RuleOnly(DispatchRule.Fifo), [machine],
            Job(1, new JobStep(10, 1, 5 * Hour, "ALU")),
            Job(2, new JobStep(10, 1, 5 * Hour, "ALU")));

        var schedule = new DispatchScheduler().Run(context, [0, 1], DueDateAssigner.Assign(context), Ct);
        var second = schedule.Operations.Single(o => o.JobId == 2);

        Assert.Equal(Day + 8 * Hour, second.StartSeconds);
        Assert.Equal(0, second.SetupSeconds);
    }

    /// <summary>
    /// Earliest finish, not earliest free: with two slots, the job goes to the one
    /// that already ran its family until the change-over is cheap enough to make
    /// the free slot finish first. This pins the crossover, not one side of it.
    /// </summary>
    [Theory]
    [InlineData(3600, 1, Hour)]          // 1 h setup: slot 1 finishes at 3 h against slot 0's 4 h
    [InlineData(7200, 1, Hour)]          // 2 h setup: both finish at 4 h, and slot 1 starts earlier
    [InlineData(10800, 0, 3 * Hour)]     // 3 h setup: the change-over finally costs more than the wait
    public void The_slot_choice_crosses_over_with_the_change_over_cost(long setup, int expectedSlot, long expectedStart)
    {
        var machine = Machine(1, capacity: 2) with
        {
            SetupDurations = [new SetupDuration("STEEL", "ALU", setup)]
        };
        var context = Context(RuleOnly(DispatchRule.Fifo), [machine],
            Job(1, new JobStep(10, 1, 3 * Hour, "ALU")),     // occupies slot 0, leaves it on ALU
            Job(2, new JobStep(10, 1, Hour, "STEEL")),       // occupies slot 1, leaves it on STEEL
            Job(3, new JobStep(10, 1, Hour, "ALU")));        // chooses

        var schedule = new DispatchScheduler().Run(context, [0, 1, 2], DueDateAssigner.Assign(context), Ct);
        var third = schedule.Operations.Single(o => o.JobId == 3);

        Assert.Equal(expectedSlot, third.SlotIndex);
        Assert.Equal(expectedStart, third.StartSeconds);
        Feasibility.AssertFeasible(schedule, context);
    }

    // ----- zero-length steps -----

    /// <summary>
    /// A zero-second inspection gate after a step that fills the shift used to be
    /// pushed to 08:00 the next day, because the placement search rejects a block
    /// that starts at the window end — costing the job 22 hours and, through
    /// precedence, every step behind it.
    /// </summary>
    [Fact]
    public void A_zero_duration_step_at_a_window_boundary_does_not_cost_a_day()
    {
        var machine = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(8 * Hour, 16 * Hour)],
            CalendarPeriodSeconds = Day
        };
        var context = Context(RuleOnly(DispatchRule.Fifo), [machine],
            Job(1, Step(10, 1, 8 * Hour), Step(20, 1, 0)));

        var schedule = new DispatchScheduler().Run(context, [0], DueDateAssigner.Assign(context), Ct);
        var gate = schedule.Operations.Single(o => o.StepNumber == 20);

        Assert.Equal(16 * Hour, gate.StartSeconds);
        Assert.Equal(16 * Hour, gate.EndSeconds);
        Assert.Equal(16 * Hour, schedule.MakespanSeconds);
    }

    [Fact]
    public void A_zero_duration_step_is_not_pushed_past_a_blackout_either()
    {
        var machine = Machine(1) with { Blackouts = [new CapacityBlackout(100, 10_000, "shutdown")] };
        var context = Context(RuleOnly(DispatchRule.Fifo), [machine],
            Job(1, Step(10, 1, 100), Step(20, 1, 0)));

        var schedule = new DispatchScheduler().Run(context, [0], DueDateAssigner.Assign(context), Ct);

        Assert.Equal(100, schedule.MakespanSeconds);
        Assert.Equal(100, schedule.Operations.Single(o => o.StepNumber == 20).StartSeconds);
    }

    // ----- blackout semantics, pinned deliberately -----

    /// <summary>
    /// A blackout covering exactly a break the machine was closed for anyway still
    /// moves the operation to the next day. That is a modelling choice, not an
    /// oversight — a maintenance stop means the machine is gone, so a half-finished
    /// part cannot sit in it — and it is pinned here with the cost stated, because
    /// the blackout removes zero open seconds and still costs a day of capacity.
    /// </summary>
    [Fact]
    public void A_blackout_over_a_bridgeable_break_costs_a_day_by_design()
    {
        var machine = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(8 * Hour, 12 * Hour), new CapacityWindow(12 * Hour + 1800, 16 * Hour)],
            CalendarPeriodSeconds = Day,
            MaxBridgeableGapSeconds = 1800
        };
        var withBlackout = machine with { Blackouts = [new CapacityBlackout(12 * Hour, 12 * Hour + 1800, "maintenance")] };

        long openWithout = machine.OpenSecondsWithin(Day);
        long openWith = withBlackout.OpenSecondsWithin(Day);
        Assert.Equal(openWithout, openWith);   // the blackout removes no open time at all

        var context = Context(RuleOnly(DispatchRule.Fifo), [withBlackout], Job(1, Step(10, 1, 5 * Hour)));
        var op = new DispatchScheduler().Run(context, [0], DueDateAssigner.Assign(context), Ct).Operations.Single();

        Assert.Equal(Day + 8 * Hour, op.StartSeconds);
    }

    // ----- open time -----

    /// <summary>
    /// The closed-form roll-up has to agree with walking the calendar, including
    /// across phases, partial periods and blackouts.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3 * Hour)]
    [InlineData(23 * Hour)]
    public void Open_time_matches_a_direct_walk_of_the_windows(long phase)
    {
        var machine = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(6 * Hour, 10 * Hour), new CapacityWindow(14 * Hour, 22 * Hour)],
            CalendarPeriodSeconds = Day,
            CalendarPhaseSeconds = phase
        };

        for (long until = 0; until <= 5 * Day; until += 1237)
            Assert.Equal(WalkOpenSeconds(machine, until), machine.OpenSecondsWithin(until));
    }

    private static long WalkOpenSeconds(MachineCapacity machine, long untilSeconds)
    {
        long open = 0;
        for (long t = 0; t < untilSeconds; t++)
        {
            long inPeriod = (t + machine.CalendarPhaseSeconds) % machine.CalendarPeriodSeconds;
            if (machine.AvailabilityWindows.Any(w => inPeriod >= w.StartSeconds && inPeriod < w.EndSeconds))
                open++;
        }

        return open;
    }
}
