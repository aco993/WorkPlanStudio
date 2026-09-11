using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services.Scheduling;

/// <summary>
/// How far a scheduling run has got, in the terms the page shows: how many
/// multi-start descents have finished, and the best objective value found so far.
/// </summary>
/// <param name="RestartsCompleted">Descents finished.</param>
/// <param name="TotalRestarts">Descents this run will make in total.</param>
/// <param name="BestPenalty">
/// Best weighted penalty found so far, or <see cref="double.PositiveInfinity"/>
/// before the first descent has finished.
/// </param>
/// <remarks>
/// This is the engine's <see cref="SearchProgress"/> narrowed to what a person can
/// act on. The neighbour-evaluation count is deliberately left out: it moves in
/// thousands, tells a planner nothing, and would make the indicator jitter.
/// </remarks>
public readonly record struct ScheduleRunProgress(int RestartsCompleted, int TotalRestarts, double BestPenalty)
{
    /// <summary>Completed share of the run, 0..1. A run with nothing to schedule is complete at once.</summary>
    public double Fraction => TotalRestarts <= 0 ? 1 : Math.Clamp((double)RestartsCompleted / TotalRestarts, 0, 1);

    /// <summary>The same as whole per cent, for <c>aria-valuenow</c> and the bar's width.</summary>
    public int Percent => (int)Math.Round(Fraction * 100);

    /// <summary>Whether <see cref="BestPenalty"/> yet refers to a schedule.</summary>
    public bool HasBest => double.IsFinite(BestPenalty);

    internal static ScheduleRunProgress From(SearchProgress progress) =>
        new(progress.RestartsCompleted, progress.TotalRestarts, progress.BestPenalty);
}

/// <summary>
/// Runs the scheduling engine on behalf of the app, so that nothing above this
/// line knows how — or on which thread — the search is actually executed.
/// <para>
/// The seam exists because the honest answer to "where does the search run?" is
/// host-specific and may change. On WebAssembly today it runs on the one UI
/// thread in cooperative slices (<see cref="CooperativeScheduleRunner"/>, and see
/// ADR 0019 for the measurements behind that); a host with real threads, or a
/// future browser build with working WebAssembly threading, would supply a
/// different implementation without any other file changing.
/// </para>
/// </summary>
public interface IScheduleRunner
{
    /// <summary>
    /// Runs the multi-start search on <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The prepared instance.</param>
    /// <param name="progress">
    /// Reported once before the first descent and once after each one, so a caller
    /// can render a determinate indicator. May be <c>null</c>.
    /// </param>
    /// <param name="cancellationToken">
    /// Honoured between descents as well as inside one. Cancelling throws
    /// <see cref="OperationCanceledException"/> rather than returning a partial
    /// schedule: half a plan looks exactly like a whole one, and a planner has no
    /// way to tell them apart.
    /// </param>
    Task<SchedulingResult> RunAsync(
        SchedulingContext context,
        IProgress<ScheduleRunProgress>? progress,
        CancellationToken cancellationToken);
}
