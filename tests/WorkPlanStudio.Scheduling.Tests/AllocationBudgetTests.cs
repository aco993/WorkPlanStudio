using WorkPlanStudio.Scheduling.Testing;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// Allocation tripwires. Like <see cref="PerformanceBudgetTests"/> these are not
/// measurements — <c>tests/WorkPlanStudio.Benchmarks</c> measures — but the
/// budgets here are tight rather than generous, because the thing they guard is
/// an absolute claim: a candidate schedule costs nothing to evaluate. A run that
/// starts allocating per candidate again is a regression of a different kind from
/// one that got slower, and it will not show up as a failing correctness test.
/// <para>
/// The numbers to beat: before the workspace split, one dispatch of the 100-job
/// problem allocated 67 088 B and a whole run allocated 1.05 GB over its 16 008
/// candidates.
/// </para>
/// </summary>
public class AllocationBudgetTests
{
    private static SchedulingContext Medium(int multiStart, int localSearch) =>
        ProblemFactory.Build(jobs: 100, operationsPerJob: 6, workCenters: 10, capacity: 2, multiStart, localSearch);

    private static long Measure(Action work)
    {
        work();   // JIT and any one-off statics first

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// A whole run of the 100-job problem, budget included, must stay inside a
    /// fixed number of kilobytes: the workspace, the orders and one materialised
    /// schedule, none of which scale with the number of candidates.
    /// </summary>
    [Fact]
    public void A_full_medium_run_allocates_a_fixed_amount_whatever_the_budget()
    {
        long small = Measure(() => new SchedulingEngine().Run(Medium(multiStart: 8, localSearch: 500)));
        long large = Measure(() => new SchedulingEngine().Run(Medium(multiStart: 8, localSearch: 8_000)));

        Assert.True(large < 512 * 1024, $"a 64 008-candidate run allocated {large} B");

        // Sixteen times the candidates must not cost sixteen times the memory; a
        // little more is fine (a longer search keeps more incumbent orders alive).
        Assert.True(large < small * 3, $"{small} B at 500 steps against {large} B at 8 000 steps");
    }

    /// <summary>
    /// The per-candidate figure itself, computed the way <c>docs/PERFORMANCE.md</c>
    /// should: allocated bytes divided by the candidates the run actually built,
    /// which is restarts × (one initial dispatch + the descent steps it used).
    /// </summary>
    [Fact]
    public void A_candidate_costs_under_a_hundred_bytes()
    {
        var context = Medium(multiStart: 8, localSearch: 4_000);
        SchedulingResult? result = null;

        long allocated = Measure(() => result = new SchedulingEngine().Run(context));
        long candidates = result!.LocalSearchSteps + context.Parameters.MultiStartRuns;

        double perCandidate = (double)allocated / candidates;
        Assert.True(candidates > 20_000, $"expected a large candidate count, got {candidates}");
        Assert.True(perCandidate < 100, $"{perCandidate:F1} B per candidate over {candidates} candidates");
    }

    /// <summary>
    /// Scoring an order into a workspace allocates nothing at all — not "little",
    /// nothing. This is the property the whole split exists for, so it is asserted
    /// exactly rather than with a budget.
    /// </summary>
    [Fact]
    public void Scoring_an_order_into_a_workspace_allocates_nothing()
    {
        var context = Medium(multiStart: 1, localSearch: 0);
        var dueByJob = DueDateAssigner.Assign(context);
        var workspace = SchedulingWorkspace.For(context, dueByJob);
        var order = PriorityOrdering.For(context, dueByJob);
        var scheduler = new DispatchScheduler();

        long allocated = Measure(() =>
        {
            for (int i = 0; i < 200; i++)
                _ = scheduler.Score(context, order, workspace);
        });

        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// The explanation is five more searches, so it used to cost more than the run
    /// it explains — 527 MB and 162 ms on a browser UI thread, for one sentence.
    /// </summary>
    [Fact]
    public void An_explanation_costs_a_fraction_of_a_megabyte()
    {
        var context = Medium(multiStart: 8, localSearch: 2_000);
        var result = new SchedulingEngine().Run(context);

        long probed = Measure(() => ScheduleExplainer.Explain(context, result));
        long unprobed = Measure(() => ScheduleExplainer.Explain(context, result, probeAlternativeRules: false));

        Assert.True(probed < 4 * 1024 * 1024, $"Explain with the rule probe allocated {probed} B");
        Assert.True(unprobed < 128 * 1024, $"Explain without the rule probe allocated {unprobed} B");
    }
}
