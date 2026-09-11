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
    /// <remarks>
    /// Six sorts and no validation. This used to build a whole
    /// <see cref="SchedulingContext"/> per rule just to change one enum, re-running
    /// every input check including the per-step calendar fit — five times on every
    /// engine run, and twenty-five more on every call to
    /// <see cref="ScheduleExplainer.Explain"/>.
    /// </remarks>
    public static IReadOnlyList<DispatchRule> EquivalentRules(
        SchedulingContext context, IReadOnlyDictionary<int, long> dueByJob)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dueByJob);

        var chosen = For(context.Jobs, context.Parameters.DispatchRule, dueByJob);
        var others = new List<DispatchRule>();

        foreach (var rule in Enum.GetValues<DispatchRule>())
        {
            if (rule == context.Parameters.DispatchRule)
                continue;

            // An empty instance has one order — the empty one — under every rule,
            // so every other rule is equivalent. Reporting none was a special case
            // that contradicted the definition.
            if (For(context.Jobs, rule, dueByJob).AsSpan().SequenceEqual(chosen))
                others.Add(rule);
        }

        return others;
    }

    /// <summary>Indices into <see cref="SchedulingContext.Jobs"/>, highest priority first.</summary>
    public static int[] For(SchedulingContext context, IReadOnlyDictionary<int, long> dueByJob)
    {
        ArgumentNullException.ThrowIfNull(context);
        return For(context.Jobs, context.Parameters.DispatchRule, dueByJob);
    }

    /// <summary>Indices into <paramref name="jobs"/> under one rule, highest priority first.</summary>
    public static int[] For(IReadOnlyList<ProductionJob> jobs, DispatchRule rule, IReadOnlyDictionary<int, long> dueByJob)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(dueByJob);

        int n = jobs.Count;
        var order = new int[n];
        var keys = new SortKey[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = i;
            keys[i] = new SortKey(KeyFor(rule, jobs[i], dueByJob), jobs[i].Id);
        }

        // A total, comparable key rather than a comparison delegate: Array.Sort is
        // not stable, so the id has to be part of the key, and the delegate form
        // allocated a closure over the key and job arrays on every call.
        Array.Sort(keys, order);
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

            // No 1e-9 floor: the context rejects a weight that is not finite and
            // positive, so the fudge that used to hide those inputs is gone with
            // them.
            DispatchRule.WeightedShortestProcessingTime => total / job.Weight,
            _ => total
        };
    }

    /// <summary>The rule key plus the job id, so the sort is total and needs no comparer instance.</summary>
    private readonly record struct SortKey(double Key, int JobId) : IComparable<SortKey>
    {
        public int CompareTo(SortKey other)
        {
            int c = Key.CompareTo(other.Key);
            return c != 0 ? c : JobId.CompareTo(other.JobId);
        }
    }
}
