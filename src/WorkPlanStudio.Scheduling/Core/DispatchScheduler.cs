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
                    SetupSeconds = placement.SetupSeconds,
                    PausedSeconds = placement.PausedSeconds
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
        var (start, end) = FirstFit(machine, earliest, occupied);

        return new Placement(slot, start, end, setup, end - start - occupied);
    }

    /// <summary>
    /// Earliest placement at or after <paramref name="earliest"/> for a block of
    /// <paramref name="occupied"/> busy seconds: inside one availability window
    /// of the repeating calendar — or a run of windows joined by gaps the work
    /// center allows an operation to pause across — and touching no blackout.
    /// Returns start and end; the end exceeds <c>start + occupied</c> by exactly
    /// the pauses taken.
    /// </summary>
    /// <remarks>
    /// The calendar repeats and the blackout list is finite, so this always
    /// terminates with a placement: each blackout can push the search forward at
    /// most once, and past the last one the periodic fit stands. That is why this
    /// returns a value instead of failing - a throw here would abort an entire
    /// search rather than rejecting one candidate order.
    /// </remarks>
    internal static (long Start, long End) FirstFit(MachineCapacity machine, long earliest, long occupied)
    {
        long candidate = earliest;
        while (true)
        {
            var (start, end) = FirstPeriodicFit(machine, candidate, occupied);

            var blocking = FirstBlackoutOverlapping(machine.Blackouts, start, end);
            if (blocking is null)
                return (start, end);

            // Blackouts are sorted, so resuming at this one's end can only move
            // forward - the loop makes progress and ends once it is past them all.
            candidate = blocking.EndSeconds;
        }
    }

    private static (long Start, long End) FirstPeriodicFit(MachineCapacity machine, long earliest, long occupied)
    {
        var windows = machine.AvailabilityWindows;
        if (windows.Count == 0)
            return (earliest, earliest + occupied);

        // Work on the calendar's own axis: shift by the phase, fit, shift back.
        long period = machine.CalendarPeriodSeconds;
        long phase = machine.CalendarPhaseSeconds;
        long shifted = earliest + phase;
        long cycleStart = shifted / period * period;
        int count = windows.Count;

        // Try every window start over three periods: the construction-time check
        // guarantees a run long enough exists, so one starting within two periods
        // of `earliest` must fit, and a run may itself span into a third.
        for (int first = 0; first < count * 3; first++)
        {
            long firstOffset = cycleStart + first / count * period;
            var window = windows[first % count];
            long start = Math.Max(shifted, firstOffset + window.StartSeconds);
            long windowEnd = firstOffset + window.EndSeconds;
            if (start >= windowEnd)
                continue;

            long remaining = occupied;
            long cursor = start;
            long previousEnd = windowEnd;
            int next = first;
            while (true)
            {
                long available = previousEnd - cursor;
                if (remaining <= available)
                    return (start - phase, cursor + remaining - phase);

                remaining -= available;
                next++;
                long nextOffset = cycleStart + next / count * period;
                var nextWindow = windows[next % count];
                long nextStart = nextOffset + nextWindow.StartSeconds;
                if (nextStart - previousEnd > machine.MaxBridgeableGapSeconds)
                    break;   // the gap ends the run; try the next window as a start

                cursor = nextStart;
                previousEnd = nextOffset + nextWindow.EndSeconds;
            }
        }

        // Unreachable while the construction-time fit check holds.
        throw new InvalidOperationException(
            $"Work center {machine.WorkCenterId} has no calendar slot for a {occupied}s block.");
    }

    /// <summary>The first blackout (in order) that overlaps <c>[start, end)</c>, or null.</summary>
    private static CapacityBlackout? FirstBlackoutOverlapping(IReadOnlyList<CapacityBlackout> blackouts, long start, long end)
    {
        // Binary search for the first blackout that ends after `start`; sorted and
        // non-overlapping is a construction invariant of the context.
        int lo = 0, hi = blackouts.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (blackouts[mid].EndSeconds <= start) lo = mid + 1;
            else hi = mid;
        }

        return lo < blackouts.Count && blackouts[lo].Overlaps(start, end) ? blackouts[lo] : null;
    }

    private sealed class SlotState
    {
        public long FreeAt { get; set; }
        public string? SetupFamily { get; set; }
    }

    private readonly record struct Placement(int Slot, long Start, long End, long SetupSeconds, long PausedSeconds);
}
