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
        IReadOnlyDictionary<int, long> dueByJob)
    {
        // Each work center keeps one "free at" clock per parallel slot.
        var slotFreeAt = new Dictionary<int, long[]>(context.Machines.Count);
        foreach (var machine in context.Machines.Values)
            slotFreeAt[machine.WorkCenterId] = new long[machine.ParallelCapacity];

        var operations = new List<ScheduledOperation>();
        var jobOutcomes = new List<JobSchedule>(jobPriorityOrder.Count);

        foreach (var jobIndex in jobPriorityOrder)
        {
            var job = context.Jobs[jobIndex];
            long jobReadyAt = job.ReleaseSeconds;

            foreach (var step in job.Steps)
            {
                var slots = slotFreeAt[step.WorkCenterId];
                int slot = EarliestSlot(slots);

                long start = Math.Max(jobReadyAt, slots[slot]);
                long end = start + step.DurationSeconds;

                slots[slot] = end;
                jobReadyAt = end;

                operations.Add(new ScheduledOperation(
                    job.Id, step.StepNumber, step.WorkCenterId, slot, start, end));
            }

            long due = dueByJob.TryGetValue(job.Id, out var d) ? d : jobReadyAt;
            jobOutcomes.Add(new JobSchedule(job.Id, job.Reference, job.ReleaseSeconds, due, jobReadyAt));
        }

        return new Schedule(operations, jobOutcomes);
    }

    /// <summary>Index of the slot that frees up first (lowest index wins ties — deterministic).</summary>
    private static int EarliestSlot(long[] slots)
    {
        int best = 0;
        for (int i = 1; i < slots.Length; i++)
            if (slots[i] < slots[best])
                best = i;
        return best;
    }
}
