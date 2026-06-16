namespace WorkPlanStudio.Scheduling;

/// <summary>
/// The scored quality of a schedule. The KPIs are exact (integer seconds); the
/// single <see cref="Penalty"/> scalar is what multi-start and local search
/// minimise — lower is better.
/// </summary>
public sealed record ScheduleEvaluation
{
    /// <summary>When the last operation finishes.</summary>
    public long MakespanSeconds { get; init; }

    /// <summary>Sum of every job's tardiness (lateness floored at 0).</summary>
    public long TotalTardinessSeconds { get; init; }

    /// <summary>The worst single job tardiness.</summary>
    public long MaxTardinessSeconds { get; init; }

    /// <summary>How many jobs finish after their target.</summary>
    public int LateJobCount { get; init; }

    /// <summary>Total number of jobs scheduled.</summary>
    public int JobCount { get; init; }

    /// <summary>Fraction of jobs that meet their target, 0..1 (1 when there are no jobs).</summary>
    public double OnTimeRate { get; init; }

    /// <summary>Mean throughput time (release → completion), in seconds.</summary>
    public double AverageFlowSeconds { get; init; }

    /// <summary>Busy ÷ available per work center that ran at least one op, 0..1.</summary>
    public IReadOnlyDictionary<int, double> UtilizationByWorkCenter { get; init; }
        = new Dictionary<int, double>();

    /// <summary>Mean utilisation across the work centers that were used, 0..1.</summary>
    public double AverageUtilization { get; init; }

    /// <summary>The weighted objective the search minimises (lower is better).</summary>
    public double Penalty { get; init; }
}
