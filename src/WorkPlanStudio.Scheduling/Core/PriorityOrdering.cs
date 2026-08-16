namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Turns a <see cref="DispatchRule"/> into an initial job priority order — the
/// permutation the dispatch scheduler consumes and local search refines. Jobs
/// are sorted by a rule-specific key (ascending = higher priority), with the job
/// id as a deterministic tie-break so the order never depends on input ordering.
/// </summary>
public static class PriorityOrdering
{
    /// <summary>
    /// The other dispatch rules that yield the identical order for this instance —
    /// the ones a user could select instead and see no change at all.
    /// <para>
    /// Several rule pairs coincide by construction rather than by accident. TWK
    /// targets are a strictly increasing function of processing time, so EDD and
    /// SPT are the same sort; CON gives every job the same target, so EDD becomes
    /// FIFO. Computing this from the orders themselves rather than from a table of
    /// known identities means it cannot drift away from what the engine does.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DispatchRule> EquivalentRules(
        SchedulingContext context, IReadOnlyDictionary<int, long> dueByJob)
    {
        var chosen = For(context, dueByJob);
        var others = new List<DispatchRule>();

        foreach (var rule in Enum.GetValues<DispatchRule>())
        {
            if (rule == context.Parameters.DispatchRule)
                continue;

            var candidate = context.Parameters with { DispatchRule = rule };
            if (For(new SchedulingContext(context.Jobs, [.. context.Machines.Values], candidate), dueByJob)
                .AsSpan().SequenceEqual(chosen))
                others.Add(rule);
        }

        return others;
    }

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
