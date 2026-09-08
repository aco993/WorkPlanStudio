using System.Diagnostics;

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
    private static SchedulingContext Medium(bool calendars) => Problems.Build(jobs: 100, operationsPerJob: 6, workCenters: 10, capacity: 2, multiStart: 8, localSearch: 2_000, withCalendars: calendars);

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
        var context = Problems.Build(jobs: 250, operationsPerJob: 8, workCenters: 20, capacity: 2, multiStart: 1, localSearch: 0);
        _ = new SchedulingEngine().Run(context);

        var stopwatch = Stopwatch.StartNew();
        _ = new SchedulingEngine().Run(context);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"large rule-only dispatch took {stopwatch.ElapsedMilliseconds} ms, budget 500 ms");
    }
}

/// <summary>Generated routing-shaped problems, the same shape the benchmarks and the scenario tool use.</summary>
internal static class Problems
{
    private const long Day = 24 * 3600;

    public static SchedulingContext Build(int jobs, int operationsPerJob, int workCenters, int capacity, int multiStart, int localSearch, bool withCalendars = false)
    {
        var machines = Enumerable.Range(1, workCenters)
            .Select(id => withCalendars ? TwoShiftMachine(id, capacity) : new MachineCapacity(id, $"WC-{id:00}", capacity))
            .ToList();

        var jobList = Enumerable.Range(1, jobs)
            .Select(jobId => new ProductionJob
            {
                Id = jobId,
                Reference = $"JOB-{jobId:0000}",
                ReleaseSeconds = jobId % 7 * 60L,
                Weight = 1 + jobId % 10,
                Steps = Enumerable.Range(1, operationsPerJob)
                    .Select(step => new JobStep(step, (jobId * 3 + step * 5) % workCenters + 1, 60L + (jobId * 37L + step * 53L) % 3_600L))
                    .ToList()
            })
            .ToList();

        return new SchedulingContext(jobList, machines, new SchedulingParameters
        {
            DispatchRule = DispatchRule.EarliestDueDate,
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 2,
            MultiStartRuns = multiStart,
            LocalSearchMaxSteps = localSearch,
            Seed = 20260712
        });
    }

    private static MachineCapacity TwoShiftMachine(int id, int capacity)
    {
        var windows = new List<CapacityWindow>();
        for (int day = 0; day < 5; day++)
        {
            long d = day * Day;
            windows.Add(new CapacityWindow(d + 6 * 3600, d + 10 * 3600));
            windows.Add(new CapacityWindow(d + 10 * 3600 + 1800, d + 14 * 3600));
            windows.Add(new CapacityWindow(d + 14 * 3600, d + 18 * 3600));
            windows.Add(new CapacityWindow(d + 18 * 3600 + 1800, d + 22 * 3600));
        }

        return new MachineCapacity(id, $"WC-{id:00}", capacity)
        {
            AvailabilityWindows = windows,
            CalendarPeriodSeconds = 7 * Day,
            CalendarPhaseSeconds = 6 * 3600,
            MaxBridgeableGapSeconds = 1800,
            Blackouts = [new CapacityBlackout(10 * Day, 11 * Day, "holiday")]
        };
    }
}
