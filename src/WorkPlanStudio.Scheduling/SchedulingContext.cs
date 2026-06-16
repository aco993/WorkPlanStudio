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

        var byId = new Dictionary<int, MachineCapacity>(machines.Count);
        foreach (var m in machines)
        {
            if (m.ParallelCapacity < 1)
                throw new ArgumentException($"Work center {m.WorkCenterId} has invalid capacity {m.ParallelCapacity} (must be ≥ 1).");
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

                if (!byId.ContainsKey(step.WorkCenterId))
                    throw new ArgumentException($"Job {job.Id} step {step.StepNumber} references unknown work center {step.WorkCenterId}.");
            }
        }

        Jobs = jobs;
        Machines = byId;
        Parameters = parameters;
    }

    /// <summary>Parallel-slot count for a work center (defaults to 1 if unknown).</summary>
    public int CapacityOf(int workCenterId) =>
        Machines.TryGetValue(workCenterId, out var m) ? m.ParallelCapacity : 1;
}
