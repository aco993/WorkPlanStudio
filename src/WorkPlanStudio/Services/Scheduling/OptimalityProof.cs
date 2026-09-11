using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services.Scheduling;

/// <summary>What one attempt to prove the schedule optimal established.</summary>
public enum OptimalityProofStatus
{
    /// <summary>The instance is larger than the solver accepts; nothing was attempted.</summary>
    TooLarge = 0,

    /// <summary>Proved: no schedule scores better than the one on screen.</summary>
    ScheduleIsOptimal = 1,

    /// <summary>Proved: a better schedule exists, and the solver found it.</summary>
    BetterScheduleExists = 2,

    /// <summary>The search ran out of budget. The optimum lies between the bound and the incumbent.</summary>
    NotProved = 3,

    /// <summary>Nothing to schedule.</summary>
    NothingToProve = 4
}

/// <summary>
/// The outcome of asking "is the schedule on screen the best one?".
/// <para>
/// The question is worth asking out loud because the honest answer is usually
/// "no, and here is by how much": the heuristic searches the order jobs are
/// dispatched in, and the best dispatch order is not the best schedule. Showing
/// the gap is the difference between a planner trusting the number and a planner
/// being told to.
/// </para>
/// </summary>
/// <param name="Status">What was established.</param>
/// <param name="HeuristicPenalty">The objective value of the schedule the page is showing.</param>
/// <param name="OptimalPenalty">The proved optimum, when it was proved.</param>
/// <param name="BestBound">The largest value proved to be a lower bound on the optimum.</param>
/// <param name="Operations">How many operations the instance has — the size that decides feasibility.</param>
/// <param name="Nodes">Search nodes explored.</param>
/// <param name="Elapsed">Wall-clock time the solve took.</param>
public sealed record OptimalityProof(
    OptimalityProofStatus Status,
    double HeuristicPenalty,
    double? OptimalPenalty,
    double BestBound,
    int Operations,
    long Nodes,
    TimeSpan Elapsed)
{
    /// <summary>
    /// How far above the proved optimum the schedule on screen is, in per cent.
    /// <c>null</c> unless the optimum was proved; 0 when the two coincide.
    /// </summary>
    public double? GapPercent => OptimalPenalty is { } optimum && optimum > 1e-9
        ? Math.Max(0, (HeuristicPenalty - optimum) / optimum * 100.0)
        : OptimalPenalty is not null ? 0 : null;

    /// <summary>
    /// How far above the <i>proved lower bound</i> the schedule on screen is, in
    /// per cent — an upper bound on how much could still be gained, and the only
    /// honest number a search that ran out of budget can offer. The optimum lies
    /// between the bound and the schedule, so the true gap is at most this.
    /// </summary>
    public double? BoundGapPercent => BestBound > 1e-9 && HeuristicPenalty >= BestBound
        ? (HeuristicPenalty - BestBound) / BestBound * 100.0
        : null;

    /// <summary>Nothing was computed, so nothing should be shown.</summary>
    public static OptimalityProof NotAttempted(OptimalityProofStatus status, int operations) =>
        new(status, 0, null, 0, operations, 0, TimeSpan.Zero);
}
