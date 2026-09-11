namespace WorkPlanStudio.Scheduling.Exact;

/// <summary>
/// Solves the job shop as a scheduling problem and says how much it proved.
/// <para>
/// This is what <see cref="ExactDispatchOrderOptimizer"/> is not. That class is
/// exact over the <i>n!</i> orders the greedy dispatcher can be handed, which is
/// a small and awkwardly shaped subset of the feasible schedules — on a
/// two-job, three-work-center instance its answer is 50 % worse than the real
/// optimum. This one fixes the routing arcs, branches on the order in which
/// operations use a work center, and prunes with lower bounds, so what it returns
/// with <see cref="ExactSolutionStatus.Optimal"/> is the optimum of the model the
/// rest of the engine optimises: the same target dates, the same change-over
/// matrix, the same calendars, the same weighted penalty.
/// </para>
/// <para>
/// It is exponential and says so. The limits in <see cref="ExactSolverOptions"/>
/// are not advisory — hitting them downgrades the answer to
/// <see cref="ExactSolutionStatus.FeasibleWithGap"/> with the bound that was
/// actually proved, and an instance larger than
/// <see cref="ExactSolverOptions.MaxOperations"/> is refused rather than answered
/// approximately under an exact-sounding name.
/// </para>
/// <para>
/// Measured on a desktop runtime (.NET 10, Release), on makespan-dominated job
/// shops — the hard case, because tight target dates often make the job bound the
/// optimum and the proof instant. Up to eighteen operations, five of five
/// instances are proved inside a second. At twenty-one and twenty-four, three of
/// five are proved inside a second and the other two are not proved inside a
/// minute either. Size is not the whole story: an instance whose optimum a single
/// routing already determines is proved in a few hundred nodes at thirty-five
/// operations. <c>docs/adr/0015-exact-solver.md</c> carries both sweeps and the
/// commands that produced them.
/// </para>
/// </summary>
public static class ExactJobShopSolver
{
    /// <summary>
    /// Solves <paramref name="context"/> to proven optimality, or to the best
    /// schedule and bound the limits allowed.
    /// </summary>
    /// <param name="context">The instance — jobs, work centers, calendars, parameters.</param>
    /// <param name="options">Limits and propagation settings; <see cref="ExactSolverOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Checked at every node, so a browser can abandon a solve.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The instance is larger than the options admit. The solver refuses it; it
    /// does not fall back to a heuristic and keep the name.
    /// </exception>
    public static ExactSolution Solve(
        SchedulingContext context,
        ExactSolverOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = options ?? ExactSolverOptions.Default;
        settings.Validate();

        if (context.TotalSteps > settings.MaxOperations)
            throw new ArgumentOutOfRangeException(
                nameof(context),
                context.TotalSteps,
                $"The exact solver accepts at most {settings.MaxOperations} operations; this instance has {context.TotalSteps}.");

        if (context.TotalSlots > settings.MaxSlots)
            throw new ArgumentOutOfRangeException(
                nameof(context),
                context.TotalSlots,
                $"The exact solver accepts at most {settings.MaxSlots} parallel slots; this instance has {context.TotalSlots}.");

        var dueByJob = DueDateAssigner.Assign(context);
        var instance = new ExactInstance(context, dueByJob);
        return new DisjunctiveBranchAndBound(instance, settings).Solve(cancellationToken);
    }

    /// <summary>True when <paramref name="context"/> is inside the size the options admit.</summary>
    /// <remarks>
    /// For callers that would rather ask than catch — a UI deciding whether to
    /// offer "prove this is optimal" next to the schedule, for instance.
    /// </remarks>
    public static bool CanSolve(SchedulingContext context, ExactSolverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = options ?? ExactSolverOptions.Default;
        return context.TotalSteps <= settings.MaxOperations && context.TotalSlots <= settings.MaxSlots;
    }
}
