using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services.Scheduling;

/// <summary>
/// Answers "is this schedule the best one?" for instances small enough to prove.
/// <para>
/// Separate from <see cref="IProductionScheduleService"/> on purpose: generating
/// a schedule is what the page does every time, and proving one optimal is a
/// deliberate, bounded, occasionally expensive act the user asks for. Mixing
/// them would make every run pay for a question most runs do not ask.
/// </para>
/// </summary>
public interface IOptimalityProver
{
    /// <summary>
    /// Runs the heuristic and the exact solver over the same instance and reports
    /// what could be proved. Never throws for an instance that is merely too
    /// large — that is a status, not an error.
    /// </summary>
    /// <param name="parameters">The same parameters the visible schedule was produced with.</param>
    /// <param name="cancellationToken">Cancels the solve; checked at every node.</param>
    Task<OptimalityProof> ProveAsync(SchedulingParameters parameters, CancellationToken cancellationToken = default);
}
