namespace WorkPlanStudio.Scheduling.Tests;

public sealed class AlgorithmAdversarialTests
{
    [Fact]
    public void Dispatcher_chooses_the_slot_with_earliest_completion()
    {
        var context = Scenario.Context(new SchedulingParameters(), [Scenario.Machine(1, capacity: 2)],
            Scenario.Job(1, Scenario.Step(10, 1, 100)),
            Scenario.Job(2, Scenario.Step(10, 1, 50)),
            Scenario.Job(3, Scenario.Step(10, 1, 20)));

        var schedule = new DispatchScheduler().Run(context, [0, 1, 2], FarDue(context));
        var third = schedule.Operations.Single(operation => operation.JobId == 3);

        Assert.Equal(1, third.SlotIndex);
        Assert.Equal(50, third.StartSeconds);
        Assert.Equal(70, third.EndSeconds);
        Feasibility.AssertFeasible(schedule, context);
    }

    [Fact]
    public void Sequence_setup_time_must_fit_inside_the_same_availability_window()
    {
        var machine = Scenario.Machine(1) with
        {
            AvailabilityWindows = [new CapacityWindow(0, 120)],
            SetupDurations = [new SetupDuration("A", "B", 20)]
        };
        var context = Scenario.Context(new SchedulingParameters(), [machine],
            Scenario.Job(1, new JobStep(10, 1, 0, "A")),
            Scenario.Job(2, new JobStep(10, 1, 100, "B")));

        var schedule = new DispatchScheduler().Run(context, [0, 1], FarDue(context));
        var second = schedule.Operations.Single(operation => operation.JobId == 2);

        Assert.Equal(20, second.SetupSeconds);
        Assert.Equal(0, second.StartSeconds);
        Assert.Equal(120, second.EndSeconds);
        Feasibility.AssertFeasible(schedule, context);
    }

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
        var context = Scenario.Context(parameters, [Scenario.Machine(1)],
            Scenario.DueAt(1, 90, Scenario.Step(10, 1, 100)),
            Scenario.DueAt(2, 200, Scenario.Step(10, 1, 10)),
            Scenario.DueAt(3, 40, Scenario.Step(10, 1, 20)),
            Scenario.DueAt(4, 100, Scenario.Step(10, 1, 30)));
        var due = DueDateAssigner.Assign(context);
        var scheduler = new DispatchScheduler();
        var bestPenalty = double.PositiveInfinity;
        var order = Enumerable.Range(0, context.Jobs.Count).ToArray();

        foreach (var permutation in Permutations(order))
        {
            var schedule = scheduler.Run(context, permutation, due);
            bestPenalty = Math.Min(bestPenalty, ScheduleEvaluator.Evaluate(schedule, context).Penalty);
        }

        var exact = ExactDispatchOrderOptimizer.Run(context, TestContext.Current.CancellationToken);

        Assert.Equal(24, exact.EvaluatedOrders);
        Assert.Equal(bestPenalty, exact.Result.Evaluation.Penalty, precision: 12);
        Feasibility.AssertFeasible(exact.Result.Schedule, context);
    }

    [Fact]
    public void Engine_observes_cancellation_after_a_scheduler_invocation()
    {
        using var cancellation = new CancellationTokenSource();
        var context = Scenario.Context(new SchedulingParameters { MultiStartRuns = 2, LocalSearchMaxSteps = 0 },
            [Scenario.Machine(1)],
            Scenario.Job(1, Scenario.Step(10, 1, 10)),
            Scenario.Job(2, Scenario.Step(10, 1, 10)));
        var scheduler = new CancellingScheduler(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            new SchedulingEngine(scheduler).RunCancellable(context, cancellation.Token));
        Assert.Equal(1, scheduler.InvocationCount);
    }

    private static Dictionary<int, long> FarDue(SchedulingContext context) =>
        context.Jobs.ToDictionary(job => job.Id, _ => long.MaxValue / 4);

    private static IEnumerable<int[]> Permutations(int[] values)
    {
        var current = (int[])values.Clone();
        return Generate(0);

        IEnumerable<int[]> Generate(int index)
        {
            if (index == current.Length)
            {
                yield return (int[])current.Clone();
                yield break;
            }

            for (var candidate = index; candidate < current.Length; candidate++)
            {
                (current[index], current[candidate]) = (current[candidate], current[index]);
                foreach (var permutation in Generate(index + 1))
                    yield return permutation;
                (current[index], current[candidate]) = (current[candidate], current[index]);
            }
        }
    }

    private sealed class CancellingScheduler(CancellationTokenSource cancellation) : IScheduler
    {
        private readonly IScheduler _inner = new DispatchScheduler();

        public int InvocationCount { get; private set; }
        public string Name => "cancelling test scheduler";

        public Schedule Run(
            SchedulingContext context,
            IReadOnlyList<int> jobPriorityOrder,
            IReadOnlyDictionary<int, long> dueByJob) =>
            RunCancellable(context, jobPriorityOrder, dueByJob, CancellationToken.None);

        public Schedule RunCancellable(
            SchedulingContext context,
            IReadOnlyList<int> jobPriorityOrder,
            IReadOnlyDictionary<int, long> dueByJob,
            CancellationToken cancellationToken)
        {
            var schedule = _inner.RunCancellable(context, jobPriorityOrder, dueByJob, cancellationToken);
            InvocationCount++;
            cancellation.Cancel();
            return schedule;
        }
    }
}
