namespace WorkPlanStudio.Scheduling;

/// <summary>How far a multi-start search has got, as of the last completed restart.</summary>
/// <param name="RestartsCompleted">Descents finished.</param>
/// <param name="TotalRestarts">Descents this run will make in total.</param>
/// <param name="BestPenalty">
/// The best penalty found so far, or <see cref="double.PositiveInfinity"/> before
/// the first restart has finished.
/// </param>
/// <param name="StepsUsed">Neighbours evaluated across the restarts that have finished.</param>
public readonly record struct SearchProgress(
    int RestartsCompleted,
    int TotalRestarts,
    double BestPenalty,
    int StepsUsed)
{
    /// <summary>Share of the restarts that have finished, 0..1. An instance with no jobs is complete at once.</summary>
    public double Fraction => TotalRestarts <= 0 ? 1 : (double)RestartsCompleted / TotalRestarts;

    /// <summary>Whether <see cref="BestPenalty"/> yet refers to a schedule.</summary>
    public bool HasBest => double.IsFinite(BestPenalty);
}

/// <summary>
/// One multi-start search, driven a restart at a time.
/// <para>
/// <see cref="SchedulingEngine.RunCancellable"/> is this class run to completion in
/// a single call, which is what a desktop caller wants. A browser caller cannot
/// afford that: WebAssembly has one thread, so a synchronous run of any length
/// freezes the tab, and a <see cref="CancellationToken"/> passed into it can never
/// be signalled because nothing else gets to execute. Handing the restart boundary
/// back to the caller lets it yield to the event loop between descents — which is
/// where the progress figures and a Cancel button that works come from.
/// </para>
/// <para>
/// The restart is the natural unit: the state that carries across one is three
/// arrays and a penalty, and restart 0 is always the pure rule order, so a run
/// stopped after any number of restarts is still a valid schedule rather than a
/// partial one. It is not a free choice of granularity — a descent is atomic — so
/// the responsiveness a caller can buy is bounded by how long one descent takes.
/// </para>
/// </summary>
public sealed class MultiStartRun
{
    private readonly IOrderEvaluator _evaluator;
    private readonly SchedulingWorkspace _workspace;
    private readonly int[] _baseOrder;
    private readonly int[] _order;
    private readonly int[] _bestOrder;

    private double _bestPenalty = double.PositiveInfinity;
    private int _stepsUsed;

    internal MultiStartRun(
        IScheduler scheduler, SchedulingContext context, IReadOnlyDictionary<int, long> dueByJob)
    {
        Context = context;
        DueByJob = dueByJob;
        _evaluator = LocalSearch.EvaluatorFor(scheduler, dueByJob);
        _workspace = SchedulingWorkspace.For(context, dueByJob);

        // No jobs is not zero restarts of a normal run: there is nothing to
        // perturb, so the empty schedule is the answer and the search never runs.
        TotalRestarts = context.Jobs.Count == 0 ? 0 : context.Parameters.MultiStartRuns;
        _baseOrder = TotalRestarts == 0 ? [] : PriorityOrdering.For(context, dueByJob);
        _order = new int[_baseOrder.Length];
        _bestOrder = new int[_baseOrder.Length];
    }

    /// <summary>The instance being scheduled.</summary>
    public SchedulingContext Context { get; }

    /// <summary>The target date assigned to each job, computed once before the search.</summary>
    public IReadOnlyDictionary<int, long> DueByJob { get; }

    /// <summary>Descents this run will make; 0 for an instance with no jobs.</summary>
    public int TotalRestarts { get; }

    /// <summary>Descents finished so far.</summary>
    public int RestartsCompleted { get; private set; }

    /// <summary>Whether every restart has run.</summary>
    public bool IsComplete => RestartsCompleted >= TotalRestarts;

    /// <summary>A snapshot of the run for a progress indicator.</summary>
    public SearchProgress Progress => new(RestartsCompleted, TotalRestarts, _bestPenalty, _stepsUsed);

    /// <summary>
    /// Runs the next restart and returns whether one was left to run. Allocates
    /// nothing: every candidate is scored into the shared workspace.
    /// </summary>
    public bool RunNextRestart(CancellationToken cancellationToken = default)
    {
        if (IsComplete)
            return false;

        cancellationToken.ThrowIfCancellationRequested();

        // Restart 0 is the pure rule order, so the chosen schedule can never be
        // worse than what the dispatch rule alone produces.
        Array.Copy(_baseOrder, _order, _order.Length);
        if (RestartsCompleted > 0)
            DeterministicRandom.ForRun(Context.Parameters.Seed, RestartsCompleted).Shuffle(_order);

        var start = _evaluator.Score(Context, _order, _workspace, cancellationToken);
        var descent = LocalSearch.Descend(
            _evaluator, Context, _workspace, _order, start,
            Context.Parameters.LocalSearchMaxSteps, Context.Parameters.LocalSearchAcceptance, cancellationToken);
        _stepsUsed += descent.StepsUsed;

        // Strict improvement, so restart 0 keeps ties and the result does not
        // depend on how many restarts were configured.
        if (descent.Penalty < _bestPenalty)
        {
            _bestPenalty = descent.Penalty;
            Array.Copy(_order, _bestOrder, _order.Length);
        }

        RestartsCompleted++;
        return true;
    }

    /// <summary>
    /// Runs whatever restarts are left, then materialises the winning order and
    /// rolls up the result. Running to completion here is what keeps a caller that
    /// does not care about progress a one-liner.
    /// </summary>
    public SchedulingResult Complete(CancellationToken cancellationToken = default)
    {
        var outcome = CompleteOutcome(cancellationToken);
        return new SchedulingResult(outcome.Schedule, outcome.Evaluation, DueByJob, outcome.StepsUsed)
        {
            EquivalentRules = PriorityOrdering.EquivalentRules(Context, DueByJob)
        };
    }

    /// <summary>The same, without the equivalent-rule roll-up, for callers that only need the score.</summary>
    internal SchedulingEngine.SearchOutcome CompleteOutcome(CancellationToken cancellationToken)
    {
        while (RunNextRestart(cancellationToken))
        {
        }

        // One materialised schedule per run, for the order that won.
        _evaluator.Score(Context, _bestOrder, _workspace, cancellationToken);
        var schedule = _evaluator.Materialise(Context, _bestOrder, _workspace);
        return new SchedulingEngine.SearchOutcome(schedule, ScheduleEvaluator.Evaluate(schedule, Context), _stepsUsed);
    }
}
