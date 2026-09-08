using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Benchmarks;

/// <summary>
/// Generated routing-shaped problems, the same shape the scenario tool in
/// <c>tools/</c> uses, so the two report comparable sizes. Deterministic:
/// every parameter set builds the same problem.
/// </summary>
public static class ProblemFactory
{
    private const long Day = 24 * 3600;

    public static SchedulingContext Build(int jobs, int operationsPerJob, int workCenters, int capacity, int multiStart, int localSearch, bool withCalendars = false)
    {
        var machines = Enumerable.Range(1, workCenters)
            .Select(id => withCalendars ? TwoShiftMachine(id, capacity) : new MachineCapacity(id, $"WC-{id:00}", capacity))
            .ToList();

        var steps = Enumerable.Range(1, jobs)
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

        return new SchedulingContext(steps, machines, new SchedulingParameters
        {
            DispatchRule = DispatchRule.EarliestDueDate,
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 2,
            MultiStartRuns = multiStart,
            LocalSearchMaxSteps = localSearch,
            Seed = 20260712
        });
    }

    /// <summary>Two shifts Monday–Friday with a 30-minute break each, a weekly period, one blackout day and bridgeable breaks.</summary>
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
