using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Scheduling.Testing;

/// <summary>
/// Generated routing-shaped problems: the single definition the unit tests, the
/// benchmarks and the scenario tool all build from, so "the medium problem" means
/// the same 100 jobs × 6 steps everywhere a number is reported for it.
/// Deterministic — every parameter set builds the same instance.
/// </summary>
public static class ProblemFactory
{
    private const long Day = 24 * 3600;

    /// <summary>The default seed; fixed so two runs of the same shape are the same instance.</summary>
    public const int DefaultSeed = 20260712;

    /// <summary>Builds a routing-shaped instance of the given shape.</summary>
    /// <param name="jobs">Number of jobs.</param>
    /// <param name="operationsPerJob">Steps in each job's routing.</param>
    /// <param name="workCenters">Number of work centers.</param>
    /// <param name="capacity">Parallel slots per work center.</param>
    /// <param name="multiStart">Restarts the search may use.</param>
    /// <param name="localSearch">Neighbour evaluations per restart.</param>
    /// <param name="withCalendars">Whether the work centers carry a two-shift calendar.</param>
    /// <param name="acceptance">Which improving neighbour a pass adopts.</param>
    /// <param name="seed">PRNG seed for the restarts.</param>
    /// <param name="variant">
    /// Shifts the routing, durations and releases to a different instance of the
    /// same shape. 0 is the reference instance every published number is for;
    /// anything else is for comparing two settings over more than one anecdote.
    /// </param>
    public static SchedulingContext Build(
        int jobs,
        int operationsPerJob,
        int workCenters,
        int capacity,
        int multiStart,
        int localSearch,
        bool withCalendars = false,
        LocalSearchAcceptance acceptance = LocalSearchAcceptance.BestInsertion,
        int seed = DefaultSeed,
        int variant = 0)
    {
        var machines = Enumerable.Range(1, workCenters)
            .Select(id => withCalendars ? TwoShiftMachine(id, capacity) : new MachineCapacity(id, $"WC-{id:00}", capacity))
            .ToList();

        var jobList = Enumerable.Range(1, jobs)
            .Select(jobId => new ProductionJob
            {
                Id = jobId,
                Reference = $"JOB-{jobId:0000}",
                ReleaseSeconds = (jobId + variant) % 7 * 60L,
                Weight = 1 + (jobId + variant) % 10,
                Steps = Enumerable.Range(1, operationsPerJob)
                    .Select(step => new JobStep(
                        step,
                        (jobId * 3 + step * 5 + variant * 7) % workCenters + 1,
                        60L + (jobId * 37L + step * 53L + variant * 101L) % 3_600L))
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
            LocalSearchAcceptance = acceptance,
            Seed = seed
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
