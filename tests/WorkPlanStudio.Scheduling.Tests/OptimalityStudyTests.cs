using WorkPlanStudio.Scheduling.Exact;
using WorkPlanStudio.Scheduling.Testing;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// The twenty-instance study, as an assertion instead of a sentence in a README.
/// <para>
/// Eight documents claimed "0.2 % mean gap, 19 of 20 instances solved exactly" and
/// attributed it to a test that computed neither figure on neither set. This test
/// runs the set, and every number it pins is one
/// <c>dotnet run -c Release --project tools/WorkPlanStudio.Scheduling.Scenarios -- exact</c>
/// prints. Both the solver and the engine are deterministic, so the figures are
/// pinned to six decimal places rather than to a comfortable round number: a change
/// that moves them is a change that has to re-run the tool and update ADR 0015.
/// </para>
/// </summary>
public class OptimalityStudyTests
{
    private static readonly OptimalityStudy.Summary Study = OptimalityStudy.Run();

    [Fact]
    public void The_set_spans_the_feature_space_the_engine_claims_to_model()
    {
        var instances = OptimalityInstances.All();

        Assert.Equal(20, instances.Count);
        Assert.Equal(20, instances.Select(i => i.Name).Distinct().Count());

        // Each of these is a modelling feature the engine ships and the audit
        // observed the old generators never varying.
        Assert.Contains(instances, i => i.Context.Jobs.Any(j => j.ReleaseSeconds > 0));
        Assert.Contains(instances, i => i.Context.Machines.Values.Any(m => m.ParallelCapacity > 1));
        Assert.Contains(instances, i => i.Context.Machines.Values.Any(m => m.SetupDurations.Count > 0));
        Assert.Contains(instances, i => i.Context.Machines.Values.Any(m => m.AvailabilityWindows.Count > 0));
        Assert.Contains(instances, i => i.Context.Machines.Values.Any(m => m.Blackouts.Count > 0));
        Assert.Contains(instances, i => i.Context.Machines.Values.Any(m => m.MaxBridgeableGapSeconds > 0));
        Assert.Contains(instances, i => i.Context.Jobs.Any(j => j.ExplicitDueSeconds is not null));
        Assert.True(instances.Select(i => i.Context.Parameters.DueDateRule).Distinct().Count() >= 4);
        Assert.True(instances.Select(i => i.Context.Parameters.DispatchRule).Distinct().Count() >= 3);
        Assert.True(instances.Select(i => i.JobCount).Distinct().Count() >= 5);
        Assert.True(instances.Select(i => i.WorkCentreCount).Distinct().Count() >= 4);
    }

    /// <summary>
    /// Every instance is solved to proved optimality under the default limits. An
    /// instance whose optimum is only a guess contributes nothing to a gap
    /// measurement, so this is a precondition for every number below.
    /// </summary>
    [Fact]
    public void Every_instance_is_proved_optimal()
    {
        Assert.Equal(20, Study.Proved);
        Assert.All(Study.Rows, row => Assert.Equal(ExactSolutionStatus.Optimal, row.Status));
    }

    /// <summary>
    /// Every optimal schedule survives the same independent feasibility check the
    /// dispatcher's schedules are held to — precedence, releases, calendars,
    /// blackouts, per-slot exclusivity and the change-over actually charged. An
    /// "optimum" that cheats on a constraint would otherwise make the whole study
    /// look like the heuristic was terrible.
    /// </summary>
    [Fact]
    public void Every_optimal_schedule_is_feasible_under_an_independent_check()
    {
        var instances = OptimalityInstances.All();
        for (int i = 0; i < instances.Count; i++)
        {
            Assert.Equal(instances[i].Name, Study.Rows[i].Name);
            Feasibility.AssertFeasible(Study.Rows[i].ExactSchedule!, instances[i].Context);
        }
    }

    [Fact]
    public void No_instance_lets_the_engine_undercut_the_optimum()
    {
        Assert.All(Study.Rows, row =>
            Assert.True(row.Heuristic >= row.Optimum - 1e-6,
                $"{row.Name}: engine {row.Heuristic:F4} beat the optimum {row.Optimum:F4}"));
    }

    /// <summary>
    /// The numbers the documentation may now quote, measured on this machine and
    /// reproducible anywhere: both halves of the pipeline are deterministic.
    /// </summary>
    [Fact]
    public void The_measured_figures_are_the_ones_the_documentation_quotes()
    {
        Assert.Equal(7, Study.SolvedExactly);
        Assert.Equal(18, Study.SearchExact);

        Assert.Equal(3.272245, Study.MeanGap, 6);
        Assert.Equal(0.053409, Study.MedianGap, 6);
        Assert.Equal(60.906977, Study.WorstGap, 6);
        Assert.Equal(3.267613, Study.MeanModelGap, 6);
        Assert.Equal(0.002707, Study.MeanSearchGap, 6);
    }

    /// <summary>
    /// The claim the whole stream exists to correct, stated as the inequality it is:
    /// the search is within a fraction of a per cent of the best schedule its
    /// dispatcher can produce, and that schedule is on average more than three times
    /// the optimum. The old headline measured the first number and reported it as
    /// the second.
    /// </summary>
    [Fact]
    public void The_search_is_near_exact_and_the_model_is_not()
    {
        Assert.True(Study.MeanSearchGap < 0.005, $"mean search gap {Study.MeanSearchGap:P3}");
        Assert.True(Study.MeanModelGap > 1.0, $"mean model gap {Study.MeanModelGap:P2}");

        // The mean is dragged by one instance whose optimum has no late job at all;
        // the median is the figure to quote next to it.
        Assert.True(Study.MedianGap < Study.MeanGap / 10, "the mean and the median should be far apart here");
    }

    /// <summary>
    /// The whole study has to stay cheap enough to run in the unit suite, because a
    /// measurement nobody re-runs goes stale exactly the way the old one did.
    /// </summary>
    [Fact]
    public void The_whole_study_solves_in_a_few_seconds()
    {
        Assert.True(Study.SolverMilliseconds < 20_000,
            $"twenty instances took {Study.SolverMilliseconds:F0} ms of solver time");
    }
}
