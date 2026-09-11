namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// How much the solver actually proved.
/// <para>
/// The distinction is the point of the type. A solver that reports "optimal"
/// after it ran out of budget is worse than no solver at all, because every
/// number downstream inherits a guarantee that was never established.
/// </para>
/// </summary>
public enum ExactSolutionStatus
{
    /// <summary>No feasible schedule was found inside the budget. Only <see cref="ExactSolution.BestBound"/> is meaningful.</summary>
    NoSolutionFound = 0,

    /// <summary>A schedule was found and the search finished: nothing better exists.</summary>
    Optimal = 1,

    /// <summary>A schedule was found, the search was cut short, and the optimum lies in <c>[BestBound, Penalty]</c>.</summary>
    FeasibleWithGap = 2
}

/// <summary>
/// What one exact solve produced: the best schedule found, the best lower bound
/// proved, and how much of the tree that took.
/// </summary>
/// <param name="Status">What was proved — see <see cref="ExactSolutionStatus"/>.</param>
/// <param name="Schedule">The best schedule found, or <c>null</c> when none was.</param>
/// <param name="Penalty">
/// The objective value of <paramref name="Schedule"/> under the context's
/// parameters, or <c>null</c> when no schedule was found.
/// </param>
/// <param name="BestBound">
/// The largest value proved to be a lower bound on the optimum. Equal to
/// <paramref name="Penalty"/> when the status is <see cref="ExactSolutionStatus.Optimal"/>.
/// </param>
/// <param name="MakespanSeconds">Makespan of <paramref name="Schedule"/>, or 0 when none was found.</param>
/// <param name="NodesExplored">Search nodes opened.</param>
/// <param name="NodesPruned">Nodes cut by the bound, by propagation or by the state memo.</param>
/// <param name="Elapsed">Wall clock spent in the solve. Reported, never used to decide anything.</param>
public sealed record ExactSolution(
    ExactSolutionStatus Status,
    Schedule? Schedule,
    double? Penalty,
    double BestBound,
    long MakespanSeconds,
    long NodesExplored,
    long NodesPruned,
    TimeSpan Elapsed)
{
    /// <summary>True when the optimum is proved, so <see cref="Penalty"/> is the optimum.</summary>
    public bool IsOptimal => Status == ExactSolutionStatus.Optimal;

    /// <summary>
    /// <c>Penalty − BestBound</c>: how much of the objective is still unproven.
    /// Zero for an optimal answer, <c>null</c> when no schedule was found.
    /// </summary>
    public double? AbsoluteGap => Penalty is { } p ? Math.Max(0, p - BestBound) : null;

    /// <summary>
    /// <see cref="AbsoluteGap"/> relative to the incumbent, the form a MILP solver
    /// prints. <c>null</c> when no schedule was found; 0 when the incumbent is 0.
    /// </summary>
    public double? RelativeGap => Penalty is { } p && AbsoluteGap is { } gap
        ? (Math.Abs(p) < 1e-12 ? 0 : gap / Math.Abs(p))
        : null;

    /// <summary>The optimum, or the throw that says it was never proved.</summary>
    /// <exception cref="InvalidOperationException">The status is not <see cref="ExactSolutionStatus.Optimal"/>.</exception>
    /// <remarks>
    /// Callers that want a number and not a maybe should use this: it makes the
    /// difference between "proved" and "found" impossible to drop by accident,
    /// which is exactly how a 50 % gap ends up quoted as an optimality result.
    /// </remarks>
    public double OptimalPenalty => Status == ExactSolutionStatus.Optimal && Penalty is { } p
        ? p
        : throw new InvalidOperationException(
            $"The solver did not prove optimality (status {Status}, best bound {BestBound:F6}).");
}
