using CsCheck;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// Invariants of the objective itself, over generated instances that vary the
/// four things the documentation makes the most noise about and the original
/// generator never touched: release times, weights, explicit target dates and
/// setup families, plus parallel capacity and zero-length steps.
/// <para>
/// These are properties a wrong model breaks, not properties the implementation
/// makes true by construction. Two of them — that a dominating schedule scores no
/// worse, and that more slack cannot increase tardiness — would have caught the
/// timeline overflow directly: it produced a negative total tardiness, which is
/// better than every achievable score.
/// </para>
/// </summary>
public class ObjectiveInvariantTests
{
    private static readonly DispatchRule[] DispatchRules = Enum.GetValues<DispatchRule>();
    private static readonly DueDateRule[] DueDateRules = Enum.GetValues<DueDateRule>();
    private static readonly string[] Families = ["STEEL", "ALU", "BRASS"];

    private sealed record GeneratedStep(int WorkCenter, int Duration, int Family);

    private sealed record GeneratedJob(GeneratedStep[] Steps, int Release, int Weight, int? Due);

    /// <summary>
    /// Zero durations are in range, capacity goes to 4, releases are staggered,
    /// weights vary and a third of the jobs carry an explicit target date.
    /// </summary>
    private static readonly Gen<SchedulingContext> GenRichContext =
        from machineCount in Gen.Int[1, 4]
        from capacities in Gen.Int[1, 4].Array[machineCount]
        from setupSeconds in Gen.Int[0, 300].Array[machineCount]
        from jobs in GenJob(machineCount).Array[1, 6]
        from ruleIndex in Gen.Int[0, DispatchRules.Length - 1]
        from dueIndex in Gen.Int[0, DueDateRules.Length - 1]
        from flowTenths in Gen.Int[10, 40]
        from multiStart in Gen.Int[1, 4]
        from localSearch in Gen.Int[0, 200]
        from seed in Gen.Int[1, 1_000_000]
        select Build(machineCount, capacities, setupSeconds, jobs, ruleIndex, dueIndex, flowTenths / 10.0, multiStart, localSearch, seed);

    private static Gen<GeneratedJob> GenJob(int machineCount) =>
        from steps in (from workCenter in Gen.Int[1, machineCount]
                       from duration in Gen.Int[0, 600]
                       from family in Gen.Int[0, Families.Length - 1]
                       select new GeneratedStep(workCenter, duration, family)).Array[1, 4]
        from release in Gen.Int[0, 1200]
        from weight in Gen.Int[1, 9]
        from hasDue in Gen.Int[0, 2]
        from dueOffset in Gen.Int[0, 4000]
        select new GeneratedJob(steps, release, weight, hasDue == 0 ? release + dueOffset : null);

    private static SchedulingContext Build(
        int machineCount, int[] capacities, int[] setupSeconds, GeneratedJob[] generated,
        int ruleIndex, int dueIndex, double flowFactor, int multiStart, int localSearch, int seed)
    {
        var machines = Enumerable.Range(1, machineCount)
            .Select(id => new MachineCapacity(id, $"WC-{id}", capacities[id - 1])
            {
                // A full matrix between the three families, so a change-over is
                // charged whenever the sequence alternates.
                SetupDurations = setupSeconds[id - 1] == 0
                    ? []
                    : [.. from leaving in Families
                          from arriving in Families
                          where !string.Equals(leaving, arriving, StringComparison.Ordinal)
                          select new SetupDuration(leaving, arriving, setupSeconds[id - 1])]
            })
            .ToList();

        var jobs = generated
            .Select((job, index) => new ProductionJob
            {
                Id = index + 1,
                Reference = $"J{index + 1}",
                ReleaseSeconds = job.Release,
                Weight = job.Weight,
                ExplicitDueSeconds = job.Due,
                Steps = job.Steps
                    .Select((s, stepIndex) => new JobStep((stepIndex + 1) * 10, s.WorkCenter, s.Duration, Families[s.Family]))
                    .ToList()
            })
            .ToList();

        return new SchedulingContext(jobs, machines, new SchedulingParameters
        {
            DispatchRule = DispatchRules[ruleIndex],
            DueDateRule = DueDateRules[dueIndex],
            TwkFlowFactor = flowFactor,
            MultiStartRuns = multiStart,
            LocalSearchMaxSteps = localSearch,
            Seed = seed
        });
    }

    // ----- the KPIs have ranges -----

    [Fact]
    public void Every_reported_kpi_stays_inside_its_documented_range() =>
        GenRichContext.Sample(ctx =>
        {
            var evaluation = new SchedulingEngine().Run(ctx).Evaluation;

            Assert.True(evaluation.TotalTardinessSeconds >= 0, $"total tardiness {evaluation.TotalTardinessSeconds}");
            Assert.True(evaluation.MaxTardinessSeconds >= 0, $"max tardiness {evaluation.MaxTardinessSeconds}");
            Assert.True(evaluation.MakespanSeconds >= 0, $"makespan {evaluation.MakespanSeconds}");
            Assert.InRange(evaluation.OnTimeRate, 0.0, 1.0);
            Assert.InRange(evaluation.AverageUtilization, 0.0, 1.0);
            foreach (var (workCenterId, utilization) in evaluation.UtilizationByWorkCenter)
                Assert.True(utilization is >= 0.0 and <= 1.0, $"work center {workCenterId} at {utilization}");
            Assert.True(double.IsFinite(evaluation.Penalty), $"penalty {evaluation.Penalty}");
            Assert.True(evaluation.Penalty >= 0, $"penalty {evaluation.Penalty}");
        });

    // ----- a second, independent makespan lower bound -----

    /// <summary>
    /// The existing bound is the longest single job's critical path, which never
    /// exercises machine contention: it is satisfied by a schedule that ignores
    /// capacity entirely. This adds the machine-load bound — total work routed to
    /// a work center, divided by its parallel slots — so the two together catch a
    /// dispatcher that overlaps work it should have queued.
    /// </summary>
    [Fact]
    public void Makespan_clears_both_the_critical_path_and_the_machine_load_bound() =>
        GenRichContext.Sample(ctx =>
        {
            var schedule = new SchedulingEngine().Run(ctx).Schedule;

            long criticalPath = ctx.Jobs.Max(j => j.ReleaseSeconds + j.TotalProcessingSeconds);

            long machineLoad = 0;
            foreach (var machine in ctx.Machines.Values)
            {
                long work = ctx.Jobs
                    .SelectMany(j => j.Steps)
                    .Where(s => s.WorkCenterId == machine.WorkCenterId)
                    .Sum(s => s.DurationSeconds);
                machineLoad = Math.Max(machineLoad, work / machine.ParallelCapacity);
            }

            long bound = Math.Max(criticalPath, machineLoad);
            Assert.True(schedule.MakespanSeconds >= bound,
                $"makespan {schedule.MakespanSeconds} is below the lower bound {bound} " +
                $"(critical path {criticalPath}, machine load {machineLoad})");
        });

    // ----- the objective is monotone in the things it is built from -----

    /// <summary>
    /// Dominance: a schedule in which no job finishes later and the makespan is no
    /// larger cannot score worse. Constructed by handing the same instance a
    /// strictly larger search budget, which can only replace the incumbent with a
    /// strictly better one.
    /// </summary>
    [Fact]
    public void A_longer_search_never_scores_worse() =>
        GenRichContext.Sample(ctx =>
        {
            double shortSearch = new SchedulingEngine().Run(
                ctx.WithParameters(ctx.Parameters with { LocalSearchMaxSteps = 0, MultiStartRuns = 1 })).Evaluation.Penalty;
            double longSearch = new SchedulingEngine().Run(
                ctx.WithParameters(ctx.Parameters with { LocalSearchMaxSteps = 400, MultiStartRuns = 4 })).Evaluation.Penalty;

            Assert.True(longSearch <= shortSearch + 1e-9, $"{longSearch} against {shortSearch}");
        });

    /// <summary>
    /// Monotonicity in slack: for a fixed order, moving every target date later
    /// cannot increase tardiness. This is a property of the model, and it fails
    /// the moment a completion or a target date wraps.
    /// </summary>
    [Fact]
    public void More_slack_never_increases_tardiness() =>
        GenRichContext.Sample(ctx =>
        {
            var tight = ctx.WithParameters(ctx.Parameters with
            {
                DueDateRule = DueDateRule.EqualSlack,
                SlackSeconds = 0,
                MultiStartRuns = 1,
                LocalSearchMaxSteps = 0
            });
            var loose = tight.WithParameters(tight.Parameters with { SlackSeconds = 86_400 });

            var scheduler = new DispatchScheduler();
            var order = PriorityOrdering.For(tight, DueDateAssigner.Assign(tight));

            long tightTardiness = ScheduleEvaluator
                .Evaluate(scheduler.Run(tight, order, DueDateAssigner.Assign(tight)), tight).TotalTardinessSeconds;
            long looseTardiness = ScheduleEvaluator
                .Evaluate(scheduler.Run(loose, order, DueDateAssigner.Assign(loose)), loose).TotalTardinessSeconds;

            Assert.True(looseTardiness <= tightTardiness,
                $"a day of extra slack raised tardiness from {tightTardiness} to {looseTardiness}");
        });

    /// <summary>
    /// Raising the late-job weight makes the <i>penalty</i> of a late job larger,
    /// which is arithmetic rather than a claim about the search. The tempting
    /// stronger version — that a harsher penalty never leaves more jobs late — is
    /// measurably false, and stated here so nobody adds it back: the engine is a
    /// heuristic over a weighted sum, so changing the weights changes which local
    /// optimum it lands in, and it can land in one with more late jobs and a lower
    /// total score. Only an exact optimiser would make the stronger version hold.
    /// </summary>
    [Fact]
    public void The_late_job_term_is_what_the_weight_says_it_is() =>
        GenRichContext.Sample(ctx =>
        {
            var evaluation = new SchedulingEngine().Run(ctx).Evaluation;
            var score = new ScheduleScore(
                evaluation.MakespanSeconds, evaluation.TotalTardinessSeconds, evaluation.LateJobCount);

            var parameters = ctx.Parameters;
            double doubled = score.Penalty(parameters with { LatePenalty = parameters.LatePenalty * 2 });

            Assert.Equal(doubled - score.Penalty(parameters), parameters.LatePenalty * score.LateJobCount, 6);
        });

    // ----- feasibility with everything switched on -----

    [Fact]
    public void Releases_weights_targets_setups_and_wide_capacity_stay_feasible_together() =>
        GenRichContext.Sample(ctx =>
        {
            var result = new SchedulingEngine().Run(ctx);
            Feasibility.AssertFeasible(result.Schedule, ctx);
            Assert.Equal(result.Schedule.Signature(), new SchedulingEngine().Run(ctx).Schedule.Signature());
        });

    /// <summary>
    /// Every rule the engine reports as equivalent has to produce the identical
    /// schedule, not merely the identical starting order — the claim a user acts
    /// on is "you would see no change".
    /// </summary>
    [Fact]
    public void Rules_reported_as_equivalent_produce_the_identical_schedule() =>
        GenRichContext.Sample(ctx =>
        {
            var result = new SchedulingEngine().Run(ctx);
            string signature = result.Schedule.Signature();

            foreach (var rule in result.EquivalentRules)
            {
                var twin = ctx.WithParameters(ctx.Parameters with { DispatchRule = rule });
                Assert.Equal(signature, new SchedulingEngine().Run(twin).Schedule.Signature());
            }
        });
}
