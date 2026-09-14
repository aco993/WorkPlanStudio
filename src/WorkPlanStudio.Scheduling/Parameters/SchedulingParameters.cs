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
    /// <remarks>
    /// Eight, and measured rather than inherited. On 48 seven-job instances whose
    /// dispatch-order optimum is exactly computable, the mean gap to that optimum
    /// falls from 9.0 % at one restart to 3.8 % at two, 1.4 % at four and 0.45 %
    /// at eight — and the number of instances more than 5 % off falls from 16 to
    /// one. On the 100-job benchmark instance the extra restarts change nothing at
    /// all, which is where the "8 restarts buy nothing" reading comes from; it is
    /// true of that instance and false of the sizes below it. The cost of being
    /// wrong in that direction is now small: eight restarts of the 100-job problem
    /// take 97 ms and allocate 92 KB, against 301 ms and 1.05 GB before.
    /// See ADR 0022.
    /// </remarks>
    public int MultiStartRuns { get; init; } = 8;

    /// <summary>
    /// Upper bound on local-search neighbour evaluations <b>per restart</b>. 0
    /// disables the polish; the total work of a run is this times
    /// <see cref="MultiStartRuns"/>, capped by
    /// <see cref="SchedulingParameterLimits.MaxTotalEvaluations"/>.
    /// </summary>
    public int LocalSearchMaxSteps { get; init; } = 2000;

    /// <summary>Which improving neighbour a local-search pass adopts.</summary>
    public LocalSearchAcceptance LocalSearchAcceptance { get; init; } = LocalSearchAcceptance.BestInsertion;

    /// <summary>Seed for the deterministic PRNG; the same seed always yields the same schedule.</summary>
    public int Seed { get; init; } = 20260616;

    // ----- Objective weights (penalty = weighted sum, lower is better, computed in hours) -----

    /// <summary>Weight on makespan (hours).</summary>
    public double MakespanWeight { get; init; } = 1.0;

    /// <summary>Weight on total tardiness (hours).</summary>
    public double TardinessWeight { get; init; } = 10.0;

    /// <summary>Flat penalty per late job — dominates so the search first reduces the number of late jobs.</summary>
    public double LatePenalty { get; init; } = 100.0;

    // There is deliberately no display section here. `MinutesPerWorkingDay` used
    // to live on this record and, by its own doc-comment, existed only to map
    // work-time onto days in a Gantt chart — a rendering constant inside the one
    // library whose headline claim is that it has no UI concerns. It is now a
    // field on the scheduling page's form, passed to the view projection directly.
}
