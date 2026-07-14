namespace WorkPlanStudio.Scheduling.Tests;

public sealed class CoverageGapTests
{
    [Fact]
    public void Validation_rejects_each_invalid_parameter_family()
    {
        var invalid = new SchedulingParameters[]
        {
            new() { MinutesPerWorkingDay = 0 },
            new() { NopSecondsPerOp = -1 },
            new() { SlackSeconds = -1 },
            new() { ConstantAllowanceSeconds = -1 },
            new() { TwkFlowFactor = double.NaN },
            new() { TwkFlowFactor = 0 },
            new() { MakespanWeight = -1 },
            new() { TardinessWeight = double.PositiveInfinity },
            new() { LatePenalty = double.NaN },
            new() { DispatchRule = (DispatchRule)999 },
            new() { DueDateRule = (DueDateRule)999 }
        };

        foreach (var parameters in invalid)
            Assert.Throws<ArgumentOutOfRangeException>(() => SchedulingParameterLimits.Validate(parameters));
    }

    [Fact]
    public void Context_rejects_invalid_calendars_setups_and_steps()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchedulingContext(
            [], [new MachineCapacity(1, "M") { AvailabilityWindows = [new CapacityWindow(-1, 1)] }], new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchedulingContext(
            [], [new MachineCapacity(1, "M") { AvailabilityWindows = [new CapacityWindow(1, 1)] }], new()));
        Assert.Throws<ArgumentException>(() => new SchedulingContext(
            [], [new MachineCapacity(1, "M") { AvailabilityWindows = [new CapacityWindow(0, 10), new CapacityWindow(5, 20)] }], new()));

        foreach (var setup in new[]
                 {
                     new SetupDuration("", "B", 0),
                     new SetupDuration("A", "", 0),
                     new SetupDuration("A", "B", -1)
                 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new SchedulingContext(
                [], [new MachineCapacity(1, "M") { SetupDurations = [setup] }], new()));

        AssertInvalidStep(new JobStep(1, 1, -1));
        AssertInvalidStep(new JobStep(1, 1, 1, ""));
        AssertInvalidStep(new JobStep(1, 1, 1, new string('x', 41)));
    }

    [Fact]
    public void Fallback_rules_and_query_helpers_are_deterministic()
    {
        var job = Scenario.Released(7, 10, Scenario.Step(1, 1, 20));
        var parameters = new SchedulingParameters { DueDateRule = (DueDateRule)999 };
        Assert.Equal(30, DueDateAssigner.DueFor(job, parameters));

        var context = Scenario.Context(
            new SchedulingParameters { DispatchRule = DispatchRule.EarliestDueDate }, [Scenario.Machine(1)], job);
        Assert.Equal([0], PriorityOrdering.For(context, new Dictionary<int, long>()));
        Assert.Equal(1, context.CapacityOf(404));

        var schedule = new Schedule(
            [
                new ScheduledOperation(1, 2, 1, 0, 20, 30),
                new ScheduledOperation(1, 1, 1, 0, 0, 10),
                new ScheduledOperation(2, 1, 2, 0, 0, 5)
            ], []);
        Assert.Equal([0L, 20L], schedule.OnWorkCenter(1).Select(operation => operation.StartSeconds));
    }

    [Fact]
    public void Due_date_scaling_detects_overflow()
    {
        var job = Scenario.Job(1, new JobStep(1, 1, long.MaxValue));
        Assert.Throws<OverflowException>(() => DueDateAssigner.DueFor(
            job, new SchedulingParameters { DueDateRule = DueDateRule.TotalWorkContent, TwkFlowFactor = 2 }));
    }

    [Fact]
    public void Empty_schedule_has_neutral_evaluation_and_explanation()
    {
        var context = new SchedulingContext([], [], new SchedulingParameters());
        var result = new SchedulingEngine().Run(context);

        Assert.Equal(1, result.Evaluation.OnTimeRate);
        Assert.Equal(0, result.Evaluation.AverageFlowSeconds);
        Assert.Equal(0, result.Evaluation.AverageUtilization);
        Assert.Null(ScheduleExplainer.Explain(context, result).Bottleneck);
    }

    [Fact]
    public void Remaining_defensive_and_fallback_paths_are_explicitly_verified()
    {
        var zeroStateRandom = new DeterministicRandom(8_482_583_892_990_087_863L);
        Assert.NotEqual(0UL, zeroStateRandom.NextUInt64());
        Assert.Equal(0, zeroStateRandom.NextInt(1));
        Assert.InRange(zeroStateRandom.NextInt(2), 0, 1);

        var job = Scenario.Job(7, Scenario.Step(1, 1, 10));
        Assert.Equal(job.TotalProcessingSeconds, PriorityOrdering.KeyFor(
            (DispatchRule)999, job, new Dictionary<int, long>()));

        var context = Scenario.Context(new SchedulingParameters(), [Scenario.Machine(1)], job);
        var scheduler = new DispatchScheduler();
        Assert.Equal("Finite-capacity dispatch", scheduler.Name);
        Assert.Equal(10, scheduler.Run(context, [0], new Dictionary<int, long>()).Jobs.Single().DueSeconds);

        var zeroLength = new Schedule([new ScheduledOperation(1, 1, 1, 0, 0, 0)], []);
        Assert.Equal(0, ScheduleEvaluator.Evaluate(zeroLength, context).UtilizationByWorkCenter[1]);

        var unknownCenterResult = new SchedulingResult(
            new Schedule([], []),
            new ScheduleEvaluation { UtilizationByWorkCenter = new Dictionary<int, double> { [404] = 0.5 } },
            new Dictionary<int, long>(),
            0);
        Assert.Equal("404", ScheduleExplainer.Explain(
            new SchedulingContext([], [], new SchedulingParameters()), unknownCenterResult).Bottleneck?.WorkCenterName);
    }

    [Fact]
    public void Setup_lookup_covers_mismatches_matches_and_missing_transitions()
    {
        var machine = new MachineCapacity(1, "M")
        {
            SetupDurations =
            [
                new SetupDuration("X", "B", 1),
                new SetupDuration("A", "C", 2),
                new SetupDuration("A", "B", 3)
            ]
        };
        var matched = Scenario.Job(1,
            new JobStep(1, 1, 10, "A"),
            new JobStep(2, 1, 10, "B"));
        var missing = Scenario.Job(2,
            new JobStep(1, 1, 10, "A"),
            new JobStep(2, 1, 10, "D"));

        var matchedResult = new SchedulingEngine().Run(Scenario.Context(new SchedulingParameters(), [machine], matched));
        var missingResult = new SchedulingEngine().Run(Scenario.Context(new SchedulingParameters(), [machine], missing));

        Assert.Equal(3, matchedResult.Schedule.Operations.Single(operation => operation.StepNumber == 2).SetupSeconds);
        Assert.Equal(0, missingResult.Schedule.Operations.Single(operation => operation.StepNumber == 2).SetupSeconds);
    }

    private static void AssertInvalidStep(JobStep step)
    {
        var job = new ProductionJob { Id = 1, Reference = "J1", Steps = [step] };
        Assert.Throws<ArgumentException>(() => new SchedulingContext(
            [job], [Scenario.Machine(1)], new SchedulingParameters()));
    }
}
