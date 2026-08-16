namespace WorkPlanStudio.Scheduling;

/// <summary>
/// The finite-capacity list scheduler. Jobs are placed in priority order; each
/// job's steps run in sequence, every step taking the placement that <b>finishes
/// earliest</b> across the work center's parallel slots.
/// <para>
/// Three invariants make every output feasible by construction:
/// <list type="bullet">
/// <item>precedence — a step never starts before the previous step of the same job finishes;</item>
/// <item>capacity — each work-center slot is strictly serial, so no work center
/// ever runs more than its <see cref="MachineCapacity.ParallelCapacity"/> operations at once;</item>
/// <item>calendar — a step occupies one contiguous block inside a single
/// availability window, change-over included.</item>
/// </list>
/// There is no floating-point and no gap back-filling, which keeps the result
/// reproducible and the reasoning simple.
/// </para>
/// <para>
/// Slot choice is by earliest finish rather than earliest free clock, because
/// change-over makes those differ: a slot that frees later but already ran this
/// operation's family can finish sooner than one that is free now but needs a
/// setup. Ties break on start, then slot index, so the result stays deterministic.
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
        // Each work center keeps one state per parallel slot: when it frees up,
        // and which operation family it last ran.
        var slotStates = new Dictionary<int, SlotState[]>(context.Machines.Count);
        foreach (var machine in context.Machines.Values)
        {
            var states = new SlotState[machine.ParallelCapacity];
            for (int i = 0; i < states.Length; i++)
                states[i] = new SlotState();
            slotStates[machine.WorkCenterId] = states;
        }

        var operations = new List<ScheduledOperation>();
        var jobOutcomes = new List<JobSchedule>(jobPriorityOrder.Count);

        foreach (var jobIndex in jobPriorityOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = context.Jobs[jobIndex];
            long jobReadyAt = job.ReleaseSeconds;

            foreach (var step in job.Steps)
            {
                var machine = context.Machines[step.WorkCenterId];
                var slots = slotStates[step.WorkCenterId];
                var placement = EarliestFinish(context, machine, slots, step, jobReadyAt);

                slots[placement.Slot].FreeAt = placement.End;
                slots[placement.Slot].SetupFamily = step.SetupFamily;
                jobReadyAt = placement.End;

                operations.Add(new ScheduledOperation(
                    job.Id, step.StepNumber, step.WorkCenterId, placement.Slot, placement.Start, placement.End)
                {
                    SetupSeconds = placement.SetupSeconds
                });
            }

            long due = dueByJob.TryGetValue(job.Id, out var d) ? d : jobReadyAt;
            jobOutcomes.Add(new JobSchedule(job.Id, job.Reference, job.ReleaseSeconds, due, jobReadyAt));
        }

        return new Schedule(operations, jobOutcomes);
    }

    private static Placement EarliestFinish(
        SchedulingContext context,
        MachineCapacity machine,
        SlotState[] slots,
        JobStep step,
        long jobReadyAt)
    {
        // ParallelCapacity >= 1 is a construction invariant, so slot 0 always
        // exists and can seed the comparison.
        var best = PlaceOn(context, machine, slots, step, jobReadyAt, slot: 0);

        for (int slot = 1; slot < slots.Length; slot++)
        {
            var candidate = PlaceOn(context, machine, slots, step, jobReadyAt, slot);
            if (candidate.End < best.End || (candidate.End == best.End && candidate.Start < best.Start))
                best = candidate;
        }

        return best;
    }

    private static Placement PlaceOn(
        SchedulingContext context,
        MachineCapacity machine,
        SlotState[] slots,
        JobStep step,
        long jobReadyAt,
        int slot)
    {
        long setup = context.SetupSecondsFor(machine.WorkCenterId, slots[slot].SetupFamily, step.SetupFamily);
        long occupied = setup + step.DurationSeconds;
        long earliest = Math.Max(jobReadyAt, slots[slot].FreeAt);
        long start = FirstFittingStart(machine, earliest, occupied);

        return new Placement(slot, start, start + occupied, setup);
    }

    /// <summary>
    /// Earliest instant at or after <paramref name="earliest"/> where a block of
    /// <paramref name="occupied"/> seconds fits wholly inside one availability
    /// window of the repeating calendar.
    /// </summary>
    /// <remarks>
    /// The calendar repeats, so this always finds a placement: the context has
    /// already proved the block fits in some window, which means a fit exists in
    /// the current period or the next one. That is why this returns a value
    /// instead of failing - a throw here would abort an entire search rather than
    /// rejecting one candidate order.
    /// </remarks>
    private static long FirstFittingStart(MachineCapacity machine, long earliest, long occupied)
    {
        var windows = machine.AvailabilityWindows;
        if (windows.Count == 0)
            return earliest;

        long period = machine.CalendarPeriodSeconds;
        long cycleStart = earliest / period * period;
        long offset = earliest - cycleStart;

        // At most two periods: either it fits in what is left of this one, or at
        // the first suitable window of the next.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var window in windows)
            {
                long candidate = Math.Max(offset, window.StartSeconds);
                if (candidate < window.EndSeconds && occupied <= window.EndSeconds - candidate)
                    return cycleStart + candidate;
            }

            cycleStart += period;
            offset = 0;
        }

        // Unreachable while the construction-time fit check holds.
        throw new InvalidOperationException(
            $"Work center {machine.WorkCenterId} has no calendar slot for a {occupied}s block.");
    }

    private sealed class SlotState
    {
        public long FreeAt { get; set; }
        public string? SetupFamily { get; set; }
    }

    private readonly record struct Placement(int Slot, long Start, long End, long SetupSeconds);
}
