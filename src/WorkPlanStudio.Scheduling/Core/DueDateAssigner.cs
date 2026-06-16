namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Assigns each job its target completion date (the "meta" / <c>Zieltermin</c>)
/// according to the chosen <see cref="DueDateRule"/>. This runs once, before
/// scheduling; the resulting targets drive the due-date dispatch rules and all
/// lateness / tardiness KPIs. All values are seconds from the horizon.
/// </summary>
public static class DueDateAssigner
{
    /// <summary>Targets keyed by job id.</summary>
    public static IReadOnlyDictionary<int, long> Assign(SchedulingContext context)
    {
        var map = new Dictionary<int, long>(context.Jobs.Count);
        foreach (var job in context.Jobs)
            map[job.Id] = DueFor(job, context.Parameters);
        return map;
    }

    /// <summary>The target completion second for a single job under the given parameters.</summary>
    public static long DueFor(ProductionJob job, SchedulingParameters p)
    {
        long release = job.ReleaseSeconds;
        long total = job.TotalProcessingSeconds;
        return p.DueDateRule switch
        {
            DueDateRule.Explicit => job.ExplicitDueSeconds ?? release + p.ConstantAllowanceSeconds,
            DueDateRule.TotalWorkContent => release + (long)Math.Round(p.TwkFlowFactor * total, MidpointRounding.ToEven),
            DueDateRule.NumberOfOperations => release + p.NopSecondsPerOp * job.StepCount,
            DueDateRule.EqualSlack => release + total + p.SlackSeconds,
            DueDateRule.ConstantAllowance => release + p.ConstantAllowanceSeconds,
            _ => release + total
        };
    }
}
