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
                Assert.Equal(step.DurationSeconds, op.DurationSeconds);
                Assert.True(op.StartSeconds >= previousEnd,
                    $"Job {job.Id} step {op.StepNumber} starts at {op.StartSeconds}, before {previousEnd}.");
                previousEnd = op.EndSeconds;
            }
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
