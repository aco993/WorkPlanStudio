namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// Cases picked to break a plausible-but-wrong implementation rather than to
/// confirm the happy path. Ported from the production-platform branch and
/// adapted to the periodic calendar model.
/// </summary>
public sealed class AlgorithmAdversarialTests
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;

    private static Dictionary<int, long> FarDue(SchedulingContext context) =>
        context.Jobs.ToDictionary(j => j.Id, _ => long.MaxValue / 4);

    /// <summary>
    /// A scheduler that picks "the slot with the lowest index that is free" gets
    /// this wrong: the third job belongs on the slot that frees at 50, not the one
    /// that frees at 100.
    /// </summary>
    [Fact]
    public void Dispatcher_chooses_the_slot_with_the_earliest_completion()
    {
        var context = Context(new SchedulingParameters(), [Machine(1, capacity: 2)],
            Job(1, Step(10, 1, 100)),
            Job(2, Step(10, 1, 50)),
            Job(3, Step(10, 1, 20)));

        var schedule = new DispatchScheduler().Run(context, [0, 1, 2], FarDue(context));
        var third = schedule.Operations.Single(o => o.JobId == 3);

        Assert.Equal(1, third.SlotIndex);
        Assert.Equal(50, third.StartSeconds);
        Assert.Equal(70, third.EndSeconds);
        Feasibility.AssertFeasible(schedule, context);
    }

    /// <summary>
    /// Change-over is part of the block, not a prefix that may hang outside the
    /// window: setup + processing has to fit between the window bounds.
    /// </summary>
    [Fact]
    public void Setup_time_must_fit_inside_the_same_availability_window()
    {
        var machine = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(0, 120)],
            CalendarPeriodSeconds = Day,
            SetupDurations = [new SetupDuration("A", "B", 20)]
        };
        var context = Context(new SchedulingParameters(), [machine],
            Job(1, new JobStep(10, 1, 0, "A")),
            Job(2, new JobStep(10, 1, 100, "B")));

        var schedule = new DispatchScheduler().Run(context, [0, 1], FarDue(context));
        var second = schedule.Operations.Single(o => o.JobId == 2);

        Assert.Equal(20, second.SetupSeconds);
        Assert.Equal(0, second.StartSeconds);
        Assert.Equal(120, second.EndSeconds);   // exactly fills the window
        Feasibility.AssertFeasible(schedule, context);
    }

    /// <summary>
    /// A second, independently written enumeration must agree with the shipped
    /// optimizer. Weights are deliberately lopsided so a wrong penalty ordering
    /// would show up.
    /// </summary>
    [Fact]
    public void Exact_optimizer_matches_an_independent_permutation_oracle()
    {
        var parameters = new SchedulingParameters
        {
            DueDateRule = DueDateRule.Explicit,
            MultiStartRuns = 1,
            LocalSearchMaxSteps = 0,
            MakespanWeight = 0.25,
            TardinessWeight = 2,
            LatePenalty = 3
        };
        var context = Context(parameters, [Machine(1)],
            DueAt(1, 90, Step(10, 1, 100)),
            DueAt(2, 200, Step(10, 1, 10)),
            DueAt(3, 40, Step(10, 1, 20)),
            DueAt(4, 100, Step(10, 1, 30)));

        var due = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();

        double oracle = double.PositiveInfinity;
        foreach (var permutation in Permutations([.. Enumerable.Range(0, context.Jobs.Count)]))
            oracle = Math.Min(oracle, ScheduleEvaluator.Evaluate(scheduler.Run(context, permutation, due), context).Penalty);

        Assert.Equal(oracle, ExactDispatchOrderOptimizer.Run(context, TestContext.Current.CancellationToken)
            .Result.Evaluation.Penalty, 9);
    }

    /// <summary>Zero-length operations must not create a placement that ends before it starts.</summary>
    [Fact]
    public void Zero_duration_steps_stay_ordered()
    {
        var context = Context(new SchedulingParameters(), [Machine(1), Machine(2)],
            Job(1, Step(10, 1, 0), Step(20, 2, 0), Step(30, 1, 0)));

        var schedule = new DispatchScheduler().Run(context, [0], FarDue(context));

        Assert.All(schedule.Operations, o => Assert.True(o.EndSeconds >= o.StartSeconds));
        Feasibility.AssertFeasible(schedule, context);
    }

    /// <summary>
    /// A release far in the future must push the job there, not wrap it back into
    /// the first calendar window of the horizon.
    /// </summary>
    [Fact]
    public void A_late_release_is_not_wrapped_back_into_an_earlier_window()
    {
        var machine = Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(8 * Hour, 16 * Hour)],
            CalendarPeriodSeconds = Day
        };
        var context = Context(new SchedulingParameters(), [machine],
            Released(1, 3 * Day + 10 * Hour, Step(10, 1, Hour)));

        var op = new DispatchScheduler().Run(context, [0], FarDue(context)).Operations.Single();

        Assert.Equal(3 * Day + 10 * Hour, op.StartSeconds);   // inside day 3's window, not day 0
    }

    private static IEnumerable<int[]> Permutations(int[] items, int fixedPrefix = 0)
    {
        if (fixedPrefix == items.Length)
        {
            yield return (int[])items.Clone();
            yield break;
        }

        for (int i = fixedPrefix; i < items.Length; i++)
        {
            (items[fixedPrefix], items[i]) = (items[i], items[fixedPrefix]);
            foreach (var permutation in Permutations(items, fixedPrefix + 1))
                yield return permutation;
            (items[fixedPrefix], items[i]) = (items[i], items[fixedPrefix]);
        }
    }
}
