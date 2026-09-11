namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// Where a block of work lands on a work center's calendar: the earliest start at
/// or after a given moment such that the block gets its seconds inside the
/// repeating availability windows, pausing only across gaps the work center
/// allows, and touching no blackout.
/// <para>
/// This is a second, independent implementation of the placement rule that
/// <c>DispatchScheduler.FirstFit</c> applies — deliberately so. The exact solver
/// is only worth having if it can disagree with the heuristic, and it cannot
/// disagree about anything it computes with the heuristic's own code.
/// <c>ExactSolverTests.The_two_placement_implementations_agree</c> holds the two
/// against each other over several thousand generated calendars, so the
/// independence is real and a divergence is a test failure rather than a silent
/// difference of opinion between the oracle and the thing it judges.
/// </para>
/// <para>
/// The property the branch-and-bound depends on is monotonicity: a later
/// <c>earliest</c> never produces an earlier end. That is what makes the
/// earliest-start schedule of a machine sequence the best schedule of that
/// sequence, which is what makes enumerating sequences an exact method.
/// </para>
/// </summary>
internal static class CalendarPlacement
{
    /// <summary>
    /// Earliest placement of <paramref name="occupied"/> busy seconds at or after
    /// <paramref name="earliest"/>. Returns the start and the end; the end exceeds
    /// <c>start + occupied</c> by exactly the seconds paused across closed time.
    /// </summary>
    /// <remarks>
    /// A block of zero busy seconds is placed at <paramref name="earliest"/>
    /// whatever the calendar says: a zero-length operation is an inspection gate
    /// or a cost-only step, it occupies no machine time, and requiring it to fall
    /// inside an open window delays it and everything behind it for no work.
    /// </remarks>
    internal static (long Start, long End) Earliest(MachineCapacity machine, long earliest, long occupied)
    {
        if (occupied <= 0)
            return (earliest, earliest);

        // Each pass clears at least one blackout, and the blackouts are sorted and
        // non-overlapping, so the loop is bounded by their count.
        long candidate = earliest;
        for (int pass = 0; pass <= machine.Blackouts.Count; pass++)
        {
            var (start, end) = InsideWindows(machine, candidate, occupied);
            long resumeAt = FirstBlackoutEndOverlapping(machine.Blackouts, start, end);
            if (resumeAt < 0)
                return (start, end);
            candidate = resumeAt;
        }

        throw new InvalidOperationException(
            $"Work center {machine.WorkCenterId} could not place a {occupied}s block clear of its blackouts.");
    }

    /// <summary>End of the first blackout that shares a second with <c>[start, end)</c>, or <c>-1</c>.</summary>
    private static long FirstBlackoutEndOverlapping(IReadOnlyList<CapacityBlackout> blackouts, long start, long end)
    {
        foreach (var blackout in blackouts)
        {
            if (blackout.StartSeconds >= end)
                break;
            if (blackout.EndSeconds > start)
                return blackout.EndSeconds;
        }

        return -1;
    }

    private static (long Start, long End) InsideWindows(MachineCapacity machine, long earliest, long occupied)
    {
        var windows = machine.AvailabilityWindows;
        int count = windows.Count;
        if (count == 0)
            return (earliest, checked(earliest + occupied));

        // Work on the calendar's own axis, where window k of cycle c is simply
        // absolute window index c * count + k. Shifting once here is what keeps
        // the phase out of every comparison below.
        long period = machine.CalendarPeriodSeconds;
        long phase = machine.CalendarPhaseSeconds;
        long axis = checked(earliest + phase);
        long firstWindow = axis / period * count;

        // Three periods is enough: the context guarantees a run long enough for
        // this block exists, so one starting within two periods of `axis` must
        // fit, and that run may itself reach into a third.
        long lastWindow = firstWindow + 3L * count;
        for (long index = firstWindow; index < lastWindow; index++)
        {
            long start = Math.Max(axis, WindowStart(windows, period, count, index));
            long segmentEnd = WindowEnd(windows, period, count, index);
            if (start >= segmentEnd)
                continue;

            long remaining = occupied;
            long cursor = start;
            long window = index;
            while (true)
            {
                long available = segmentEnd - cursor;
                if (remaining <= available)
                    return (start - phase, cursor + remaining - phase);

                remaining -= available;
                window++;
                long nextStart = WindowStart(windows, period, count, window);
                if (nextStart - segmentEnd > machine.MaxBridgeableGapSeconds)
                    break;   // the gap ends this run; try a later window as the start

                cursor = nextStart;
                segmentEnd = WindowEnd(windows, period, count, window);
            }
        }

        throw new InvalidOperationException(
            $"Work center {machine.WorkCenterId} has no calendar slot for a {occupied}s block.");
    }

    private static long WindowStart(IReadOnlyList<CapacityWindow> windows, long period, int count, long index) =>
        index / count * period + windows[(int)(index % count)].StartSeconds;

    private static long WindowEnd(IReadOnlyList<CapacityWindow> windows, long period, int count, long index) =>
        index / count * period + windows[(int)(index % count)].EndSeconds;
}
