namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Turns a <see cref="DispatchRule"/> into an initial job priority order — the
/// permutation the dispatch scheduler consumes and local search refines. Jobs
/// are sorted by a rule-specific key (ascending = higher priority), with the job
/// id as a deterministic tie-break so the order never depends on input ordering.
/// </summary>
public static class PriorityOrdering
{
    /// <summary>Indices into <see cref="SchedulingContext.Jobs"/>, highest priority first.</summary>
    public static int[] For(SchedulingContext context, IReadOnlyDictionary<int, long> dueByJob)
    {
        var jobs = context.Jobs;
        int n = jobs.Count;

        var order = new int[n];
        var key = new double[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
            key[i] = KeyFor(context.Parameters.DispatchRule, jobs[i], dueByJob);
        }

        Array.Sort(order, (a, b) =>
        {
            int c = key[a].CompareTo(key[b]);
            return c != 0 ? c : jobs[a].Id.CompareTo(jobs[b].Id);
        });
        return order;
    }

    private static double KeyFor(DispatchRule rule, ProductionJob job, IReadOnlyDictionary<int, long> dueByJob)
    {
        long total = job.TotalProcessingSeconds;
        long due = dueByJob.TryGetValue(job.Id, out var d) ? d : job.ReleaseSeconds + total;
        return rule switch
        {
            DispatchRule.Fifo => job.ReleaseSeconds,
            DispatchRule.ShortestProcessingTime => total,
            DispatchRule.LongestProcessingTime => -(double)total,
            DispatchRule.EarliestDueDate => due,
            DispatchRule.CriticalRatio => due / Math.Max(1.0, total),
            DispatchRule.WeightedShortestProcessingTime => total / Math.Max(1e-9, job.Weight),
            _ => total
        };
    }
}
