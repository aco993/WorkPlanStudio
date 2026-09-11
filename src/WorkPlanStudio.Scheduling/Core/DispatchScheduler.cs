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
/// The placement arithmetic is integer and there is no gap back-filling, which
/// keeps the result reproducible and the reasoning simple. (The objective is not
/// integer — the penalty is a weighted sum of doubles — but nothing the
/// dispatcher computes is.)
/// </para>
/// <para>
/// Slot choice is by earliest finish rather than earliest free clock, because
/// change-over makes those differ: a slot that frees later but already ran this
/// operation's family can finish sooner than one that is free now but needs a
/// setup. Ties break on start, then slot index, so the result stays deterministic.
/// </para>
/// <para>
/// The arithmetic is <c>checked</c>. It cannot overflow — the context bounds every
/// duration and the instance's whole span — so the checks cost nothing on the
/// normal path, and they turn any future hole in that validation into a loud
/// failure instead of a wrapped clock. Saturating would be worse than either: a
/// silently capped timeline still scores, and the score still looks plausible.
/// </para>
/// </summary>
public sealed class DispatchScheduler : IScheduler, IOrderEvaluator
{
    /// <inheritdoc />
    public string Name => "Finite-capacity dispatch";

    /// <inheritdoc />
    public Schedule Run(
        SchedulingContext context,
        IReadOnlyList<int> jobPriorityOrder,
        IReadOnlyDictionary<int, long> dueByJob,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(jobPriorityOrder);

        var order = jobPriorityOrder as int[] ?? [.. jobPriorityOrder];
        var workspace = SchedulingWorkspace.For(context, dueByJob);
        Dispatch(context, order, workspace, cancellationToken);
        return Materialise(context, order, workspace);
    }

    /// <inheritdoc />
    public ScheduleScore Score(
        SchedulingContext context,
        ReadOnlySpan<int> jobPriorityOrder,
        SchedulingWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.Fits(context))
            throw new ArgumentException("The workspace was built for a different instance.", nameof(workspace));

        Dispatch(context, jobPriorityOrder, workspace, cancellationToken);

        long makespan = 0;
        long totalTardiness = 0;
        int lateJobs = 0;
        var completion = workspace.CompletionByJobIndex;
        var due = workspace.DueByJobIndex;
        foreach (int jobIndex in jobPriorityOrder)
        {
            long finished = completion[jobIndex];
            if (finished > makespan)
                makespan = finished;
            if (finished > due[jobIndex])
            {
                lateJobs++;
                totalTardiness = checked(totalTardiness + (finished - due[jobIndex]));
            }
        }

        return new ScheduleScore(makespan, totalTardiness, lateJobs);
    }

    /// <inheritdoc />
    public Schedule Materialise(
        SchedulingContext context,
        ReadOnlySpan<int> jobPriorityOrder,
        SchedulingWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workspace);

        var operations = new ScheduledOperation[workspace.OperationCount];
        var stepNumbers = context.StepNumbers;
        var stepMachineIndex = context.StepMachineIndex;
        var machineIds = context.MachineIdsSorted;
        for (int i = 0; i < operations.Length; i++)
        {
            var record = workspace.Operations[i];
            operations[i] = new ScheduledOperation(
                context.Jobs[record.JobIndex].Id,
                stepNumbers[record.StepIndex],
                machineIds[stepMachineIndex[record.StepIndex]],
                record.SlotIndex,
                record.StartSeconds,
                record.EndSeconds,
                record.SetupSeconds,
                record.PausedSeconds);
        }

        // Sorted by job id, not by dispatch order: this is the collection a UI
        // binds a table to, and a table that reorders itself between two runs of
        // the same instance looks like a bug in the schedule.
        var order = jobPriorityOrder.ToArray();
        Array.Sort(order, (a, b) => context.Jobs[a].Id.CompareTo(context.Jobs[b].Id));

        var jobOutcomes = new JobSchedule[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            var job = context.Jobs[order[i]];
            jobOutcomes[i] = new JobSchedule(
                job.Id,
                job.Reference,
                job.ReleaseSeconds,
                workspace.DueByJobIndex[order[i]],
                workspace.CompletionByJobIndex[order[i]]);
        }

        return Schedule.FromOwnedArrays(operations, jobOutcomes);
    }

    private static void Dispatch(
        SchedulingContext context,
        ReadOnlySpan<int> jobPriorityOrder,
        SchedulingWorkspace workspace,
        CancellationToken cancellationToken)
    {
        workspace.Reset();

        var slotFreeAt = workspace.SlotFreeAt;
        var slotFamilyId = workspace.SlotFamilyId;
        var operations = workspace.Operations;
        var completion = workspace.CompletionByJobIndex;

        var machines = context.MachinesByIndex;
        var slotOffset = context.SlotOffsetByMachineIndex;
        var stepOffset = context.JobStepOffset;
        var stepMachineIndex = context.StepMachineIndex;
        var stepDuration = context.StepDuration;
        var stepFamilyId = context.StepFamilyId;
        var release = context.JobRelease;

        int placed = 0;
        foreach (int jobIndex in jobPriorityOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long jobReadyAt = release[jobIndex];

            int last = stepOffset[jobIndex + 1];
            for (int step = stepOffset[jobIndex]; step < last; step++)
            {
                int machineIndex = stepMachineIndex[step];
                var machine = machines[machineIndex];
                int firstSlot = slotOffset[machineIndex];
                int slotCount = slotOffset[machineIndex + 1] - firstSlot;
                long duration = stepDuration[step];
                int family = stepFamilyId[step];

                int bestSlot = 0;
                long bestStart = 0, bestEnd = 0, bestSetup = 0;
                for (int slot = 0; slot < slotCount; slot++)
                {
                    long setup = context.SetupSecondsFor(machineIndex, slotFamilyId[firstSlot + slot], family);
                    long occupied = checked(setup + duration);
                    long earliest = Math.Max(jobReadyAt, slotFreeAt[firstSlot + slot]);
                    var (start, end) = FirstFit(machine, earliest, occupied);

                    if (slot == 0 || end < bestEnd || (end == bestEnd && start < bestStart))
                    {
                        bestSlot = slot;
                        bestStart = start;
                        bestEnd = end;
                        bestSetup = setup;
                    }
                }

                slotFreeAt[firstSlot + bestSlot] = bestEnd;
                slotFamilyId[firstSlot + bestSlot] = family;
                jobReadyAt = bestEnd;

                operations[placed++] = new OperationRecord(
                    jobIndex, step, bestSlot, bestStart, bestEnd, bestSetup,
                    checked(bestEnd - bestStart - bestSetup - duration));
            }

            completion[jobIndex] = jobReadyAt;
        }

        workspace.OperationCount = placed;
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
    /// <para>
    /// The calendar repeats and the blackout list is finite, so this always
    /// terminates with a placement: each blackout can push the search forward at
    /// most once, and past the last one the periodic fit stands. That is why this
    /// returns a value instead of failing - a throw here would abort an entire
    /// search rather than rejecting one candidate order.
    /// </para>
    /// <para>
    /// A block of zero busy seconds is placed at <paramref name="earliest"/>
    /// whatever the calendar says. A zero-length step is how a routing models an
    /// inspection gate or a cost-only operation: it occupies no machine time, so
    /// requiring it to fall inside an open window would delay it — and, through
    /// precedence, every step behind it — to the next shift for no work at all.
    /// </para>
    /// </remarks>
    internal static (long Start, long End) FirstFit(MachineCapacity machine, long earliest, long occupied)
    {
        if (occupied == 0)
            return (earliest, earliest);

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
            return (earliest, checked(earliest + occupied));

        // Work on the calendar's own axis: shift by the phase, fit, shift back.
        long period = machine.CalendarPeriodSeconds;
        long phase = machine.CalendarPhaseSeconds;
        long shifted = checked(earliest + phase);
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
}
