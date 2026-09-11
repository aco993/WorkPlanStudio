namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The context's boundary. Every case here produced a silently wrong schedule
/// rather than an error, so each test states the wrong behaviour it pins against
/// rather than only the rule it checks.
/// </summary>
public class InputValidationTests
{
    private static ProductionJob JobWith(int id, string reference, double weight = 1.0, long release = 0, long? due = null) =>
        new()
        {
            Id = id,
            Reference = reference,
            Weight = weight,
            ReleaseSeconds = release,
            ExplicitDueSeconds = due,
            Steps = [Step(10, 1, 100)]
        };

    // ----- identity -----

    /// <summary>
    /// Two jobs with the same id used to share one entry in the target-date map,
    /// so the second overwrote the first's date. Measured on the audit's fixture,
    /// the penalty was 203.3056 for one input order and 0.2778 for the reverse —
    /// a factor of 731, from nothing but the order of a list.
    /// </summary>
    [Fact]
    public void Duplicate_job_ids_are_rejected()
    {
        var a = new ProductionJob { Id = 7, Reference = "A", Steps = [Step(10, 1, 100)] };
        var b = new ProductionJob { Id = 7, Reference = "B", ExplicitDueSeconds = 5, Steps = [Step(10, 1, 900)] };

        var error = Assert.Throws<ArgumentException>(() =>
            new SchedulingContext([a, b], [Machine(1)], new SchedulingParameters()));

        Assert.Contains("Duplicate job id 7", error.Message, StringComparison.Ordinal);
        Assert.Contains("'B'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Two entries for one work center used to keep the last, losing the rest of the capacity.</summary>
    [Fact]
    public void Duplicate_work_center_ids_are_rejected()
    {
        var wide = new MachineCapacity(1, "wide", 4);
        var shadow = new MachineCapacity(1, "shadow");

        var error = Assert.Throws<ArgumentException>(() =>
            new SchedulingContext([JobWith(1, "J1")], [wide, shadow], new SchedulingParameters()));

        Assert.Contains("Work center 1 is declared more than once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_step_numbers_within_one_job_are_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Context(new SchedulingParameters(), [Machine(1)], Job(1, Step(10, 1, 100), Step(10, 1, 50))));

        Assert.Contains("strictly increasing step numbers", error.Message, StringComparison.Ordinal);
    }

    // ----- weight and reference -----

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-5.0)]
    [InlineData(0.0)]
    public void A_weight_that_is_not_finite_and_positive_is_rejected(double weight)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new SchedulingContext([JobWith(1, "J1", weight)], [Machine(1)], new SchedulingParameters()));

        Assert.Contains("invalid weight", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason the bound exists: NaN sorts below every number, so a NaN-weighted
    /// job became the highest priority in the shop, and a negative weight came
    /// through <c>Math.Max(1e-9, w)</c> as a key a billion times too large, which
    /// scheduled it last.
    /// </summary>
    [Fact]
    public void A_valid_weight_still_orders_the_weighted_rule()
    {
        var context = Context(
            RuleOnly(DispatchRule.WeightedShortestProcessingTime),
            [Machine(1)],
            Weighted(1, 1.0, Step(10, 1, 1000)),
            Weighted(2, 10.0, Step(10, 1, 1000)));

        var order = PriorityOrdering.For(context, DueDateAssigner.Assign(context));

        Assert.Equal([1, 0], order);   // the heavier job has the smaller key
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_reference_is_rejected(string reference)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new SchedulingContext([JobWith(1, reference)], [Machine(1)], new SchedulingParameters()));

        Assert.Contains("reference of at most 80 characters", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_over_long_reference_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new SchedulingContext([JobWith(1, new string('x', 81))], [Machine(1)], new SchedulingParameters()));

        // Exactly at the bound is accepted, in the same way SetupFamily is capped at 40.
        _ = new SchedulingContext([JobWith(1, new string('x', 80))], [Machine(1)], new SchedulingParameters());
    }

    // ----- release and target date -----

    /// <summary>
    /// A negative release used to be accepted and then lost: the calendar walk
    /// truncates towards zero, so the window the job belonged in was never
    /// examined and it jumped forward to the first non-negative one instead.
    /// </summary>
    [Fact]
    public void A_negative_release_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new SchedulingContext([JobWith(1, "J1", release: -100_000)], [Machine(1)], new SchedulingParameters()));

        Assert.Contains("released at -100000s", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_past_the_horizon_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new SchedulingContext(
            [JobWith(1, "J1", release: SchedulingParameterLimits.MaxHorizonSeconds + 1)],
            [Machine(1)],
            new SchedulingParameters()));

    [Fact]
    public void A_negative_target_date_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new SchedulingContext(
            [JobWith(1, "J1", due: -1)], [Machine(1)], new SchedulingParameters()));

    [Fact]
    public void A_target_date_before_the_release_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() => new SchedulingContext(
            [JobWith(1, "J1", release: 5_000, due: 4_999)], [Machine(1)], new SchedulingParameters()));

        Assert.Contains("before its release", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_target_date_in_the_past_relative_to_the_work_is_allowed_and_reported_late()
    {
        // Due at the horizon, 100 s of work: physically impossible, not invalid.
        var context = Context(
            new SchedulingParameters { DueDateRule = DueDateRule.Explicit, MultiStartRuns = 1, LocalSearchMaxSteps = 0 },
            [Machine(1)],
            DueAt(1, 0, Step(10, 1, 100)));

        var result = new SchedulingEngine().Run(context);

        Assert.Equal(1, result.Evaluation.LateJobCount);
        Assert.Equal(100, result.Evaluation.TotalTardinessSeconds);
    }

    // ----- capacity boundaries -----

    [Fact]
    public void Capacity_sixty_four_is_accepted_and_sixty_five_is_not()
    {
        _ = new SchedulingContext([JobWith(1, "J1")], [new MachineCapacity(1, "wide", 64)], new SchedulingParameters());

        Assert.Throws<ArgumentException>(() =>
            new SchedulingContext([JobWith(1, "J1")], [new MachineCapacity(1, "wider", 65)], new SchedulingParameters()));
    }

    [Fact]
    public void Every_slot_of_a_wide_work_center_is_usable()
    {
        var jobs = Enumerable.Range(1, 8).Select(i => Job(i, Step(10, 1, 100))).ToArray();
        var context = Context(RuleOnly(DispatchRule.Fifo), [Machine(1, capacity: 8)], jobs);

        var schedule = new DispatchScheduler().Run(context, [.. Enumerable.Range(0, 8)], DueDateAssigner.Assign(context), Ct);

        Assert.Equal(100, schedule.MakespanSeconds);
        Assert.Equal(Enumerable.Range(0, 8), schedule.Operations.Select(o => o.SlotIndex).Order());
    }

    // ----- setup matrix -----

    /// <summary>
    /// A declared same-family change-over used to be accepted by
    /// <see cref="SetupDuration.Validate"/> and then dropped without a word by the
    /// lookup, which short-circuits <c>from == to</c>.
    /// </summary>
    [Fact]
    public void A_same_family_change_over_is_rejected_rather_than_silently_dropped()
    {
        var machine = Machine(1) with { SetupDurations = [new SetupDuration("A", "A", 600)] };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Context(RuleOnly(DispatchRule.Fifo), [machine], Job(1, new JobStep(10, 1, 100, "A"))));
    }

    [Fact]
    public void A_negative_change_over_is_rejected()
    {
        var machine = Machine(1) with { SetupDurations = [new SetupDuration("A", "B", -1)] };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Context(RuleOnly(DispatchRule.Fifo), [machine], Job(1, new JobStep(10, 1, 100, "A"))));
    }

    // ----- calendar boundaries -----

    [Fact]
    public void A_window_covering_the_whole_period_is_accepted()
    {
        var always = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(0, 86_400)],
            CalendarPeriodSeconds = 86_400
        };
        var context = Context(RuleOnly(DispatchRule.Fifo), [always], Job(1, Step(10, 1, 3600)));

        var op = new DispatchScheduler().Run(context, [0], DueDateAssigner.Assign(context), Ct).Operations.Single();

        Assert.Equal(0, op.StartSeconds);
        Assert.Equal(3600, op.EndSeconds);
        Assert.Equal(0, op.PausedSeconds);
    }

    [Fact]
    public void A_calendar_period_past_the_limit_is_rejected()
    {
        var absurd = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(0, 1000)],
            CalendarPeriodSeconds = long.MaxValue
        };

        var error = Assert.Throws<ArgumentException>(() =>
            Context(RuleOnly(DispatchRule.Fifo), [absurd], Job(1, Step(10, 1, 100))));

        Assert.Contains("calendar period", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blackout_reaching_past_the_horizon_is_rejected()
    {
        var machine = Machine(1) with
        {
            Blackouts = [new CapacityBlackout(0, SchedulingParameterLimits.MaxHorizonSeconds + 1, "forever")]
        };

        var error = Assert.Throws<ArgumentException>(() =>
            Context(RuleOnly(DispatchRule.Fifo), [machine], Job(1, Step(10, 1, 100))));

        Assert.Contains("planning horizon", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blackout_that_ends_after_the_makespan_still_only_costs_what_it_covers()
    {
        // The blackout starts after the job and swallows the rest of the horizon;
        // the job is placed before it and the KPI roll-up must not go negative.
        var machine = Machine(1) with { Blackouts = [new CapacityBlackout(1000, 10_000_000, "shutdown")] };
        var context = Context(RuleOnly(DispatchRule.Fifo), [machine], Job(1, Step(10, 1, 500)));

        var result = new SchedulingEngine().Run(context);

        Assert.Equal(500, result.Schedule.MakespanSeconds);
        Assert.Equal(1.0, result.Evaluation.UtilizationByWorkCenter[1], 9);
    }

    // ----- target dates the scheduler is handed -----

    /// <summary>
    /// A job missing from the target-date map used to fall back to its own
    /// completion time, which makes it on time by construction and worth nothing
    /// to the objective — so the search would starve exactly the job whose target
    /// had gone missing, because delaying it was free.
    /// </summary>
    [Fact]
    public void A_job_with_no_target_date_is_an_error_rather_than_never_late()
    {
        var context = Context(new SchedulingParameters(), [Machine(1)],
            Job(1, Step(10, 1, 100)), Job(2, Step(10, 1, 100)));
        var incomplete = new Dictionary<int, long> { [1] = 50 };

        var error = Assert.Throws<ArgumentException>(() =>
            new DispatchScheduler().Run(context, [0, 1], incomplete, Ct));

        Assert.Contains("No target date for job 2", error.Message, StringComparison.Ordinal);
    }

    // ----- degenerate instances -----

    [Fact]
    public void All_zero_durations_produce_a_zero_makespan_schedule()
    {
        var context = Context(new SchedulingParameters(), [Machine(1), Machine(2)],
            Job(1, Step(10, 1, 0), Step(20, 2, 0)),
            Job(2, Step(10, 2, 0), Step(20, 1, 0)));

        var result = new SchedulingEngine().Run(context);

        Assert.Equal(0, result.Schedule.MakespanSeconds);
        Assert.Equal(4, result.Schedule.Operations.Count);
        Assert.All(result.Schedule.Operations, o => Assert.Equal(0, o.DurationSeconds));
        Feasibility.AssertFeasible(result.Schedule, context);
    }

    [Fact]
    public void An_empty_instance_reports_every_other_rule_as_equivalent()
    {
        // Every rule orders nothing identically, so "no equivalent rules" was the
        // one answer that could not be right.
        var context = new SchedulingContext([], [Machine(1)], RuleOnly(DispatchRule.Fifo));

        var result = new SchedulingEngine().Run(context);

        Assert.Equal(Enum.GetValues<DispatchRule>().Length - 1, result.EquivalentRules.Count);
        Assert.DoesNotContain(DispatchRule.Fifo, result.EquivalentRules);
    }

    [Fact]
    public void A_large_instance_still_places_every_operation()
    {
        var context = Testing.ProblemFactory.Build(
            jobs: 400, operationsPerJob: 6, workCenters: 12, capacity: 2, multiStart: 1, localSearch: 0);

        var result = new SchedulingEngine().Run(context);

        Assert.Equal(2400, result.Schedule.Operations.Count);
        Assert.Equal(400, result.Schedule.Jobs.Count);
        Assert.True(result.Evaluation.TotalTardinessSeconds >= 0);
    }
}
