namespace WorkPlanStudio.Scheduling;

/// <summary>
/// The finite-capacity list scheduler. Jobs are placed in priority order; each
/// job's steps run in sequence, every step taking the <b>earliest free parallel
/// slot</b> of its work center at or after the job's running completion time.
/// <para>
/// Two invariants make every output feasible by construction:
/// <list type="bullet">
/// <item>precedence — a step never starts before the previous step of the same job finishes;</item>
/// <item>capacity — each work-center slot is strictly serial, so no work center
/// ever runs more than its <see cref="MachineCapacity.ParallelCapacity"/> operations at once.</item>
/// </list>
/// There is no floating-point and no gap back-filling, which keeps the result
/// reproducible and the reasoning simple.
/// </para>
/// </summary>
public sealed class DispatchScheduler : IScheduler
{
    /// <inheritdoc />
    public string Name => "Finite-capacity dispatch";

    /// <inheritdoc />
    public Schedule Run(
        SchedulingContext context,
        IReadOnlyList<int> jobPriorityOrder,
        IReadOnlyDictionary<int, long> dueByJob) =>
        RunCancellable(context, jobPriorityOrder, dueByJob, CancellationToken.None);

    /// <inheritdoc />
    public Schedule RunCancellable(
        SchedulingContext context,
        IReadOnlyList<int> jobPriorityOrder,
        IReadOnlyDictionary<int, long> dueByJob,
        CancellationToken cancellationToken)
    {
        // Each work center keeps one state per parallel slot.
        var slotStates = new Dictionary<int, SlotState[]>(context.Machines.Count);
        foreach (var machine in context.Machines.Values)
            slotStates[machine.WorkCenterId] = Enumerable.Range(0, machine.ParallelCapacity)
                .Select(_ => new SlotState()).ToArray();

        var operations = new List<ScheduledOperation>();
        var jobOutcomes = new List<JobSchedule>(jobPriorityOrder.Count);

        foreach (var jobIndex in jobPriorityOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = context.Jobs[jobIndex];
            long jobReadyAt = job.ReleaseSeconds;

            foreach (var step in job.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var machine = context.Machines[step.WorkCenterId];
                var slots = slotStates[step.WorkCenterId];
                var candidate = EarliestPlacement(machine, slots, step, jobReadyAt);

                slots[candidate.Slot].FreeAt = candidate.End;
                slots[candidate.Slot].SetupFamily = step.SetupFamily;
                jobReadyAt = candidate.End;

                operations.Add(new ScheduledOperation(
                    job.Id, step.StepNumber, step.WorkCenterId, candidate.Slot, candidate.Start, candidate.End)
                {
                    SetupSeconds = candidate.SetupSeconds
                });
            }

            long due = dueByJob.TryGetValue(job.Id, out var d) ? d : jobReadyAt;
            jobOutcomes.Add(new JobSchedule(job.Id, job.Reference, job.ReleaseSeconds, due, jobReadyAt));
        }

        return new Schedule(operations, jobOutcomes);
    }

    private static Placement EarliestPlacement(
        MachineCapacity machine,
        IReadOnlyList<SlotState> slots,
        JobStep step,
        long jobReadyAt)
    {
        Placement? best = null;
        for (var slot = 0; slot < slots.Count; slot++)
        {
            var setup = SetupSeconds(machine, slots[slot].SetupFamily, step.SetupFamily);
            var duration = checked(setup + step.DurationSeconds);
            var earliest = Math.Max(jobReadyAt, slots[slot].FreeAt);
            var start = NextAvailableStart(machine.AvailabilityWindows, earliest, duration);
            var candidate = new Placement(slot, start, checked(start + duration), setup);
            if (best is null || candidate.End < best.End ||
                candidate.End == best.End && candidate.Start < best.Start ||
                candidate.End == best.End && candidate.Start == best.Start && candidate.Slot < best.Slot)
                best = candidate;
        }
        return best ?? throw new InvalidOperationException($"Work center {machine.WorkCenterId} has no capacity slots.");
    }

    private static long SetupSeconds(MachineCapacity machine, string? from, string to)
    {
        if (from is null || string.Equals(from, to, StringComparison.Ordinal))
            return 0;
        return machine.SetupDurations.FirstOrDefault(item =>
            string.Equals(item.FromFamily, from, StringComparison.Ordinal) &&
            string.Equals(item.ToFamily, to, StringComparison.Ordinal))?.DurationSeconds ?? 0;
    }

    private static long NextAvailableStart(IReadOnlyList<CapacityWindow> windows, long earliest, long duration)
    {
        if (windows.Count == 0)
            return earliest;
        foreach (var window in windows)
        {
            var candidate = Math.Max(earliest, window.StartSeconds);
            if (candidate <= window.EndSeconds && duration <= window.EndSeconds - candidate)
                return candidate;
        }
        throw new InvalidOperationException("The planning horizon has insufficient calendar capacity.");
    }

    private sealed class SlotState
    {
        public long FreeAt { get; set; }
        public string? SetupFamily { get; set; }
    }

    private sealed record Placement(int Slot, long Start, long End, long SetupSeconds);
}
