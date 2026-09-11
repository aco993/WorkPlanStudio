namespace WorkPlanStudio.Scheduling;

/// <summary>
/// Which improving neighbour a local-search pass adopts. Both rules explore the
/// same insertion neighbourhood and both are strictly non-worsening; they differ
/// only in how much of the budget one adopted move costs.
/// </summary>
public enum LocalSearchAcceptance
{
    /// <summary>
    /// Adopt the first strictly improving neighbour and restart the pass from it.
    /// </summary>
    /// <remarks>
    /// A full pass is n·(n−1) neighbours, so at 100 jobs one pass is 9 900
    /// dispatches. Adopting immediately means the budget buys moves rather than
    /// comparisons, which is what makes the search scale with the instance. See
    /// ADR 0022 for the measurements that made this the default.
    /// </remarks>
    FirstImprovement,

    /// <summary>
    /// Sweep the jobs in order; move each one to its <i>best</i> position if that
    /// improves, then carry on with the next job.
    /// </summary>
    /// <remarks>
    /// A middle course: one adopted move costs n−1 evaluations rather than one
    /// pass, so a sweep can relocate every job in the sequence, and each move it
    /// makes is the best available for that job rather than the first that
    /// happened to help. See ADR 0022.
    /// </remarks>
    BestInsertion,

    /// <summary>
    /// Evaluate the whole neighbourhood and adopt the single best strictly
    /// improving neighbour.
    /// </summary>
    /// <remarks>
    /// Takes the best available step rather than the first, which is the better
    /// choice when a pass is cheap relative to the budget — small instances, or a
    /// budget set well above n². Above roughly 20 jobs the budget runs out before
    /// the pass finishes and the descent adopts one or two moves in total.
    /// </remarks>
    SteepestDescent
}
