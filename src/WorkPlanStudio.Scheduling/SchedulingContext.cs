namespace WorkPlanStudio.Scheduling;

/// <summary>
/// An immutable bundle of everything one scheduling run needs: the jobs, the work
/// centers and their capacities, and the parameters. Validates its inputs on
/// construction so the rest of the engine can assume well-formed data (the app's
/// mapping layer is responsible for filtering inactive work centers and empty
/// routings before building a context).
/// </summary>
public sealed class SchedulingContext
{
    /// <summary>The jobs to schedule (may be empty → an empty schedule).</summary>
    public IReadOnlyList<ProductionJob> Jobs { get; }

    /// <summary>Work-center capacities, keyed by work-center id.</summary>
    public IReadOnlyDictionary<int, MachineCapacity> Machines { get; }

    /// <summary>The run parameters.</summary>
    public SchedulingParameters Parameters { get; }

    /// <summary>Validates the inputs and builds an immutable scheduling context.</summary>
    public SchedulingContext(
        IReadOnlyList<ProductionJob> jobs,
        IReadOnlyList<MachineCapacity> machines,
        SchedulingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(parameters);
        SchedulingParameterLimits.Validate(parameters);

        var byId = new Dictionary<int, MachineCapacity>(machines.Count);
        var setupLookup = new Dictionary<(int WorkCenter, string From, string To), long>();

        foreach (var m in machines)
        {
            if (m.ParallelCapacity is < 1 or > 64)
                throw new ArgumentException($"Work center {m.WorkCenterId} has invalid capacity {m.ParallelCapacity} (must be 1..64).");

            if (m.AvailabilityWindows.Count > 0 && m.CalendarPeriodSeconds <= 0)
                throw new ArgumentException($"Work center {m.WorkCenterId} declares availability windows but no calendar period.");

            long previousEnd = -1;
            foreach (var window in m.AvailabilityWindows)
            {
                window.Validate();
                if (window.StartSeconds < previousEnd)
                    throw new ArgumentException($"Work center {m.WorkCenterId} availability windows must be sorted and non-overlapping.");
                if (window.EndSeconds > m.CalendarPeriodSeconds)
                    throw new ArgumentException(
                        $"Work center {m.WorkCenterId} window [{window.StartSeconds}, {window.EndSeconds}) " +
                        $"does not fit inside its {m.CalendarPeriodSeconds}s calendar period.");
                previousEnd = window.EndSeconds;
            }

            foreach (var setup in m.SetupDurations)
            {
                setup.Validate();
                // Flattened once here because the dispatcher asks for a transition
                // cost on every slot of every step of every candidate order - a
                // linear scan there would be the hottest loop in the engine.
                setupLookup[(m.WorkCenterId, setup.FromFamily, setup.ToFamily)] = setup.DurationSeconds;
            }

            byId[m.WorkCenterId] = m;
        }

        foreach (var job in jobs)
        {
            if (job.Steps.Count == 0)
                throw new ArgumentException($"Job {job.Id} ('{job.Reference}') has no steps.");

            var previous = long.MinValue;
            foreach (var step in job.Steps)
            {
                if (step.StepNumber <= previous)
                    throw new ArgumentException($"Job {job.Id} steps must have strictly increasing step numbers.");
                previous = step.StepNumber;

                if (step.DurationSeconds < 0)
                    throw new ArgumentException($"Job {job.Id} step {step.StepNumber} has negative duration.");

                if (string.IsNullOrWhiteSpace(step.SetupFamily) || step.SetupFamily.Length > 40)
                    throw new ArgumentException($"Job {job.Id} step {step.StepNumber} has an invalid setup family.");

                if (!byId.TryGetValue(step.WorkCenterId, out var machine))
                    throw new ArgumentException($"Job {job.Id} step {step.StepNumber} references unknown work center {step.WorkCenterId}.");

                // Operations are not preemptable, so a step must fit inside a
                // single availability window - worst-case change-over included.
                // Checked here rather than during dispatch: the search evaluates
                // thousands of candidate orders, and an exception thrown from
                // inside that loop would abort the whole run instead of reporting
                // an input problem the caller can act on.
                long longestWindow = machine.LongestWindowSeconds;
                if (longestWindow != long.MaxValue)
                {
                    long needed = step.DurationSeconds + machine.WorstSetupInto(step.SetupFamily);
                    if (needed > longestWindow)
                        throw new ArgumentException(
                            $"Job {job.Id} step {step.StepNumber} needs {needed}s including change-over, " +
                            $"but the longest availability window of work center {step.WorkCenterId} is {longestWindow}s.");
                }
            }

            _ = job.TotalProcessingSeconds;
        }

        Jobs = jobs;
        Machines = byId;
        Parameters = parameters;
        _setupLookup = setupLookup;
    }

    private readonly Dictionary<(int WorkCenter, string From, string To), long> _setupLookup;

    /// <summary>
    /// Change-over cost on <paramref name="workCenterId"/> when the slot last ran
    /// <paramref name="from"/> and is about to run <paramref name="to"/>. Zero for
    /// a fresh slot, for an unchanged family, or for a transition the work center
    /// does not list.
    /// </summary>
    public long SetupSecondsFor(int workCenterId, string? from, string to)
    {
        if (from is null || string.Equals(from, to, StringComparison.Ordinal))
            return 0;

        return _setupLookup.TryGetValue((workCenterId, from, to), out var seconds) ? seconds : 0;
    }

    /// <summary>Parallel-slot count for a work center (defaults to 1 if unknown).</summary>
    public int CapacityOf(int workCenterId) =>
        Machines.TryGetValue(workCenterId, out var m) ? m.ParallelCapacity : 1;
}
