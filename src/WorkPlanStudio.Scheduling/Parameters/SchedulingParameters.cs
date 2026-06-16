namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Every knob that drives a scheduling run. Immutable: callers build a new
/// instance per run (the UI binds to a mutable view-model and projects into one
/// of these). Defaults are sensible for the sample data so the page produces a
/// meaningful schedule out of the box.
/// </summary>
public sealed record SchedulingParameters
{
    // ----- Strategy -----

    /// <summary>How competing jobs are prioritised on a shared work center.</summary>
    public DispatchRule DispatchRule { get; init; } = DispatchRule.EarliestDueDate;

    /// <summary>How each job's target completion date is assigned.</summary>
    public DueDateRule DueDateRule { get; init; } = DueDateRule.TotalWorkContent;

    // ----- Due-date factors (each rule has its own; units noted) -----

    /// <summary>TWK flow factor: due = release + factor × total processing (dimensionless, ≥ 1 means allow slack).</summary>
    public double TwkFlowFactor { get; init; } = 2.0;

    /// <summary>NOP allowance per operation, in seconds.</summary>
    public long NopSecondsPerOp { get; init; } = 3600;

    /// <summary>SLK constant slack added on top of total processing, in seconds.</summary>
    public long SlackSeconds { get; init; } = 7200;

    /// <summary>CON constant allowance from release to due, in seconds.</summary>
    public long ConstantAllowanceSeconds { get; init; } = 28800; // 8 h

    // ----- Search -----

    /// <summary>Number of (re)starts; run 0 is the pure rule order, the rest are seeded perturbations. ≥ 1.</summary>
    public int MultiStartRuns { get; init; } = 8;

    /// <summary>Upper bound on local-search neighbour evaluations. 0 disables the polish.</summary>
    public int LocalSearchMaxSteps { get; init; } = 2000;

    /// <summary>Seed for the deterministic PRNG; the same seed always yields the same schedule.</summary>
    public int Seed { get; init; } = 20260616;

    // ----- Objective weights (penalty = weighted sum, lower is better, computed in hours) -----

    /// <summary>Weight on makespan (hours).</summary>
    public double MakespanWeight { get; init; } = 1.0;

    /// <summary>Weight on total tardiness (hours).</summary>
    public double TardinessWeight { get; init; } = 10.0;

    /// <summary>Flat penalty per late job — dominates so the search first reduces the number of late jobs.</summary>
    public double LatePenalty { get; init; } = 100.0;

    // ----- Display only -----

    /// <summary>Working minutes per calendar day, used solely to map work-time onto days in the Gantt chart.</summary>
    public int MinutesPerWorkingDay { get; init; } = 480; // 8 h
}
