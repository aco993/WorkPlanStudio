using System.Diagnostics;
using WorkPlanStudio.Scheduling.Testing;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// Coarse performance budgets. These are not benchmarks (see
/// <c>tests/WorkPlanStudio.Benchmarks</c> for those) but tripwires: the
/// bounds are an order of magnitude above what a laptop measures, so a
/// pass says nothing precise and a failure says something went quadratic.
/// Measured after a warm-up run so JIT time is not counted.
/// </summary>
public class PerformanceBudgetTests
{
    private static SchedulingContext Medium(bool calendars) => ProblemFactory.Build(jobs: 100, operationsPerJob: 6, workCenters: 10, capacity: 2, multiStart: 8, localSearch: 2_000, withCalendars: calendars);

    [Theory]
    [InlineData(false, 3_000)]
    [InlineData(true, 4_000)]
    public void The_medium_problem_schedules_within_its_budget(bool withCalendars, int budgetMilliseconds)
    {
        var context = Medium(withCalendars);
        _ = new SchedulingEngine().Run(context);   // warm up

        var stopwatch = Stopwatch.StartNew();
        var result = new SchedulingEngine().Run(context);
        stopwatch.Stop();

        Assert.True(result.Schedule.Operations.Count == 600, "expected every operation to be placed");
        Assert.True(stopwatch.ElapsedMilliseconds < budgetMilliseconds,
            $"medium problem (calendars: {withCalendars}) took {stopwatch.ElapsedMilliseconds} ms, budget {budgetMilliseconds} ms");
    }

    [Fact]
    public void A_single_rule_dispatch_of_the_large_problem_is_fast()
    {
        var context = ProblemFactory.Build(jobs: 250, operationsPerJob: 8, workCenters: 20, capacity: 2, multiStart: 1, localSearch: 0);
        _ = new SchedulingEngine().Run(context);

        var stopwatch = Stopwatch.StartNew();
        _ = new SchedulingEngine().Run(context);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"large rule-only dispatch took {stopwatch.ElapsedMilliseconds} ms, budget 500 ms");
    }
}
