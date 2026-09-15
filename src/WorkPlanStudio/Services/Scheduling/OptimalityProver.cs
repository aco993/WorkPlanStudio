using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Scheduling.Exact;

namespace WorkPlanStudio.Services.Scheduling;

/// <summary>
/// Builds the same instance the scheduling page just scheduled and asks the
/// exact solver what the optimum actually is.
/// <para>
/// It re-runs the heuristic rather than taking the penalty from the view model:
/// the view model carries KPIs a planner reads, not the objective the search
/// minimises, and threading one more number through it to save a hundred
/// milliseconds would trade a clear boundary for nothing.
/// </para>
/// </summary>
public sealed class OptimalityProver : IOptimalityProver
{
    private readonly ProductionOrderService _orders;
    private readonly WorkCenterService _centers;
    private readonly PlantSettingsService _settings;

    public OptimalityProver(ProductionOrderService orders, WorkCenterService centers, PlantSettingsService settings)
    {
        _orders = orders;
        _centers = centers;
        _settings = settings;
    }

    /// <summary>
    /// The budget a person waiting in front of the page will tolerate.
    /// <para>
    /// Two seconds, and the wall clock is the real limit — the node limit is
    /// raised out of the way because a node limit that stops a search which was
    /// going to finish is worse than no limit at all (measured: 50 000 nodes cut
    /// the sample plant off after 1.8 s with eight seconds of budget unused).
    /// </para>
    /// <para>
    /// It is short because this runs on the one thread the browser gives a
    /// WebAssembly app: the search is a tight loop with no hand-back, so the tab
    /// is unresponsive for exactly as long as the budget allows. Ten seconds was
    /// tried and is too long to freeze a page for. When the search cannot finish
    /// in two, the answer is still useful — the proved lower bound caps how much
    /// could be gained, and that is what the page reports.
    /// </para>
    /// </summary>
    private static ExactSolverOptions Options { get; } = ExactSolverOptions.Interactive with
    {
        NodeLimit = 4_000_000,
        TimeLimit = TimeSpan.FromSeconds(2)
    };

    /// <inheritdoc />
    public async Task<OptimalityProof> ProveAsync(
        SchedulingParameters parameters,
        CancellationToken cancellationToken = default)
    {
        SchedulingParameterLimits.Validate(parameters);

        var orders = await _orders.GetSchedulableAsync(cancellationToken);
        var centers = await _centers.GetAllAsync(cancellationToken);
        var absences = await _centers.GetAbsencesAsync(cancellationToken);
        var settings = await _settings.GetAsync(cancellationToken);
        var calendar = ShopCalendar.From(settings, centers, absences);

        var preparation = ScheduleMapper.BuildInputFromOrders(orders, centers, parameters, calendar);
        if (preparation.Input is null)
            return OptimalityProof.NotAttempted(OptimalityProofStatus.NothingToProve, 0);

        var context = preparation.Input.Context;
        int operations = context.Jobs.Sum(job => job.Steps.Count);
        if (!ExactJobShopSolver.CanSolve(context, Options))
            return OptimalityProof.NotAttempted(OptimalityProofStatus.TooLarge, operations);

        var heuristic = new SchedulingEngine().RunCancellable(context, cancellationToken).Evaluation.Penalty;
        var exact = ExactJobShopSolver.Solve(context, Options, cancellationToken);

        var status = exact.Status switch
        {
            // A proved optimum the heuristic already reached is the answer a
            // planner wants; a proved optimum it did not reach is the answer the
            // project would rather not show and has to.
            ExactSolutionStatus.Optimal when exact.Penalty is { } optimum && heuristic <= optimum + 1e-9
                => OptimalityProofStatus.ScheduleIsOptimal,
            ExactSolutionStatus.Optimal => OptimalityProofStatus.BetterScheduleExists,

            // An unproved search that never found anything better has still said
            // something about the schedule, and saying nothing about it is how
            // "not proved" came to read as "possibly much worse".
            _ when exact.Penalty is { } best && best >= heuristic - 1e-9
                => OptimalityProofStatus.NotProvedNoneBetterFound,
            _ => OptimalityProofStatus.NotProved
        };

        return new OptimalityProof(
            status,
            heuristic,
            exact.IsOptimal ? exact.Penalty : null,
            exact.BestBound,
            operations,
            exact.NodesExplored,
            exact.Elapsed,
            exact.Penalty);
    }
}
