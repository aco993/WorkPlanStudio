namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// Asserts the hard constraints that <i>every</i> schedule must satisfy, whatever
/// the parameters or seed: correct durations, operation precedence within a job,
/// no start before release, and no work center ever over its parallel capacity.
/// </summary>
internal static class Feasibility
{
    public static void AssertFeasible(Schedule schedule, SchedulingContext context)
    {
        // Precedence + release + duration, per job.
        foreach (var job in context.Jobs)
        {
            var ops = schedule.Operations
                .Where(o => o.JobId == job.Id)
                .OrderBy(o => o.StepNumber)
                .ToList();

            Assert.Equal(job.Steps.Count, ops.Count);

            long previousEnd = job.ReleaseSeconds; // first step must wait for release
            foreach (var op in ops)
            {
                var step = job.Steps.Single(s => s.StepNumber == op.StepNumber);
                Assert.Equal(step.WorkCenterId, op.WorkCenterId);
                // A placement occupies setup + processing, so the step's duration
                // is the processing part, not the whole block.
                Assert.Equal(step.DurationSeconds, op.ProcessingSeconds);
                Assert.True(op.SetupSeconds >= 0, $"Job {job.Id} step {op.StepNumber} has negative setup.");
                Assert.True(op.StartSeconds >= previousEnd,
                    $"Job {job.Id} step {op.StepNumber} starts at {op.StartSeconds}, before {previousEnd}.");
                previousEnd = op.EndSeconds;
            }
        }

        // Calendar: every placement sits wholly inside one availability window of
        // the repeating period.
        foreach (var op in schedule.Operations)
        {
            var machine = context.Machines[op.WorkCenterId];
            if (machine.AvailabilityWindows.Count == 0)
                continue;

            long period = machine.CalendarPeriodSeconds;
            long offsetStart = op.StartSeconds % period;
            long offsetEnd = offsetStart + op.DurationSeconds;

            Assert.True(
                machine.AvailabilityWindows.Any(w => offsetStart >= w.StartSeconds && offsetEnd <= w.EndSeconds),
                $"Job {op.JobId} step {op.StepNumber} runs [{op.StartSeconds}, {op.EndSeconds}) " +
                $"outside work center {op.WorkCenterId}'s availability windows.");
        }

        // Capacity, per work center: a sweep line over (start,end) intervals must
        // never have more than `capacity` operations open at once. Ends are
        // processed before starts at equal times, so back-to-back ops on the same
        // slot do not count as an overlap.
        foreach (var workCenterId in context.Machines.Keys)
        {
            int capacity = context.CapacityOf(workCenterId);
            var events = new List<(long Time, int Delta)>();
            foreach (var op in schedule.Operations.Where(o => o.WorkCenterId == workCenterId))
            {
                if (op.DurationSeconds == 0) continue;
                events.Add((op.StartSeconds, +1));
                events.Add((op.EndSeconds, -1));
            }
            events.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Delta.CompareTo(b.Delta));

            int open = 0;
            foreach (var (time, delta) in events)
            {
                open += delta;
                Assert.True(open <= capacity,
                    $"Work center {workCenterId} runs {open} operations at t={time}, over capacity {capacity}.");
            }
        }
    }
}
