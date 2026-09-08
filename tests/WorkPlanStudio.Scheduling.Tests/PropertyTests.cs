using CsCheck;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// Property-based tests: instead of a few hand-picked cases, these generate hundreds
/// of random-but-valid scheduling problems and assert the engine's <b>invariants</b>
/// hold for every one — feasibility (precedence + capacity), determinism, a makespan
/// lower bound and the "never worse than the pure rule" guarantee. On failure CsCheck
/// shrinks to a minimal counter-example and prints a seed to reproduce it.
/// </summary>
public class PropertyTests
{
    private static readonly DispatchRule[] DispatchRules = Enum.GetValues<DispatchRule>();
    private static readonly DueDateRule[] DueDateRules = Enum.GetValues<DueDateRule>();

    /// <summary>Generates a random but always-valid scheduling context.</summary>
    private static readonly Gen<SchedulingContext> GenContext =
        from machineCount in Gen.Int[1, 4]
        from capacities in Gen.Int[1, 2].Array[machineCount]
        from jobs in GenSteps(machineCount).Array[1, 6]
        from ruleIndex in Gen.Int[0, DispatchRules.Length - 1]
        from dueIndex in Gen.Int[0, DueDateRules.Length - 1]
        from flowTenths in Gen.Int[10, 40]
        from multiStart in Gen.Int[1, 4]
        from localSearch in Gen.Int[0, 200]
        from seed in Gen.Int[1, 1_000_000]
        select Build(machineCount, capacities, jobs, ruleIndex, dueIndex, flowTenths / 10.0, multiStart, localSearch, seed);

    // One job = 1..4 steps, each on some work center (1..machineCount) with a positive duration.
    private static Gen<(int WorkCenter, int Duration)[]> GenSteps(int machineCount) =>
        (from workCenter in Gen.Int[1, machineCount]
         from duration in Gen.Int[1, 600]
         select (workCenter, duration)).Array[1, 4];

    /// <summary>
    /// The same problems, but every work center also carries a repeating calendar
    /// with a random phase and a random list of blackouts. Windows are at least
    /// 900 s wide and steps at most 600 s, so every instance is constructible.
    /// </summary>
    private static readonly Gen<SchedulingContext> GenConstrainedContext =
        from baseline in GenContext
        from phases in Gen.Int[0, 3599].Array[baseline.Machines.Count]
        from blackoutStarts in Gen.Int[0, 40].Array[baseline.Machines.Count].Array[0, 3]
        from bridges in Gen.Int[0, 1].Array[baseline.Machines.Count]
        select Constrain(baseline, phases, blackoutStarts, bridges);

    private static SchedulingContext Constrain(SchedulingContext baseline, int[] phases, int[][] blackoutStartsPerRound, int[] bridges)
    {
        // One-hour period, open [0, 900) and [1800, 3600) — two windows, each big
        // enough for the largest step, with a closed gap between them. Half the
        // machines may bridge the 900 s gap, half may not.
        var machines = baseline.Machines.Values
            .OrderBy(m => m.WorkCenterId)
            .Select((m, i) => m with
            {
                AvailabilityWindows = [new CapacityWindow(0, 900), new CapacityWindow(1800, 3600)],
                CalendarPeriodSeconds = 3600,
                CalendarPhaseSeconds = phases[i],
                MaxBridgeableGapSeconds = bridges[i] == 1 ? 900 : 0,
                Blackouts = blackoutStartsPerRound
                    .Select(round => round[i])
                    .Distinct()
                    .OrderBy(start => start)
                    .Select(start => new CapacityBlackout(start * 1000L, start * 1000L + 700, $"b{start}"))
                    .ToList()
            })
            .ToList();

        return new SchedulingContext(baseline.Jobs, machines, baseline.Parameters);
    }

    private static SchedulingContext Build(
        int machineCount, int[] capacities, (int WorkCenter, int Duration)[][] jobsRaw,
        int ruleIndex, int dueIndex, double flowFactor, int multiStart, int localSearch, int seed)
    {
        var machines = Enumerable.Range(1, machineCount)
            .Select(id => new MachineCapacity(id, $"WC-{id}", capacities[id - 1]))
            .ToList();

        var jobs = jobsRaw
            .Select((steps, jobIndex) => new ProductionJob
            {
                Id = jobIndex + 1,
                Reference = $"J{jobIndex + 1}",
                Steps = steps.Select((s, stepIndex) => new JobStep((stepIndex + 1) * 10, s.WorkCenter, s.Duration)).ToList()
            })
            .ToList();

        var parameters = new SchedulingParameters
        {
            DispatchRule = DispatchRules[ruleIndex],
            DueDateRule = DueDateRules[dueIndex],
            TwkFlowFactor = flowFactor,
            MultiStartRuns = multiStart,
            LocalSearchMaxSteps = localSearch,
            Seed = seed
        };

        return new SchedulingContext(jobs, machines, parameters);
    }

    [Fact]
    public void The_same_context_always_yields_an_identical_schedule() =>
        GenContext.Sample(ctx =>
        {
            var a = new SchedulingEngine().Run(ctx);
            var b = new SchedulingEngine().Run(ctx);
            Assert.Equal(a.Schedule.Signature(), b.Schedule.Signature());
        });

    [Fact]
    public void Every_schedule_respects_operation_precedence() =>
        GenContext.Sample(ctx =>
        {
            var schedule = new SchedulingEngine().Run(ctx).Schedule;
            foreach (var jobOps in schedule.Operations.GroupBy(o => o.JobId))
            {
                var ordered = jobOps.OrderBy(o => o.StepNumber).ToList();
                for (int i = 1; i < ordered.Count; i++)
                    Assert.True(ordered[i].StartSeconds >= ordered[i - 1].EndSeconds,
                        "a step started before the previous step of the same job finished");
            }
        });

    [Fact]
    public void No_work_center_ever_runs_more_operations_than_its_capacity() =>
        GenContext.Sample(ctx =>
        {
            var schedule = new SchedulingEngine().Run(ctx).Schedule;
            foreach (var onCenter in schedule.Operations.GroupBy(o => o.WorkCenterId))
            {
                int capacity = ctx.CapacityOf(onCenter.Key);
                foreach (var slot in onCenter.GroupBy(o => o.SlotIndex))
                {
                    Assert.True(slot.Key < capacity, "an operation used a slot beyond the work center's capacity");
                    var ordered = slot.OrderBy(o => o.StartSeconds).ToList();
                    for (int i = 1; i < ordered.Count; i++)
                        Assert.True(ordered[i].StartSeconds >= ordered[i - 1].EndSeconds,
                            "two operations overlapped on the same machine slot");
                }
            }
        });

    [Fact]
    public void Calendars_phases_and_blackouts_are_respected_by_every_schedule() =>
        GenConstrainedContext.Sample(ctx =>
        {
            var result = new SchedulingEngine().Run(ctx);
            Feasibility.AssertFeasible(result.Schedule, ctx);
            Assert.Equal(result.Schedule.Signature(), new SchedulingEngine().Run(ctx).Schedule.Signature());
        });

    [Fact]
    public void Makespan_is_at_least_the_longest_single_job() =>
        GenContext.Sample(ctx =>
        {
            var schedule = new SchedulingEngine().Run(ctx).Schedule;
            long lowerBound = ctx.Jobs.Max(j => j.ReleaseSeconds + j.TotalProcessingSeconds);
            Assert.True(schedule.MakespanSeconds >= lowerBound,
                $"makespan {schedule.MakespanSeconds} is below the serial lower bound {lowerBound}");
        });

    [Fact]
    public void The_engine_is_never_worse_than_the_pure_dispatch_rule() =>
        GenContext.Sample(ctx =>
        {
            var due = DueDateAssigner.Assign(ctx);
            var ruleOrder = PriorityOrdering.For(ctx, due);
            double rulePenalty = ScheduleEvaluator.Evaluate(new DispatchScheduler().Run(ctx, ruleOrder, due), ctx).Penalty;
            double enginePenalty = new SchedulingEngine().Run(ctx).Evaluation.Penalty;
            Assert.True(enginePenalty <= rulePenalty + 1e-9,
                $"engine penalty {enginePenalty} exceeded the pure-rule penalty {rulePenalty}");
        });
}
