namespace WorkPlanStudio.Scheduling;

/// <summary>
/// The complete output of a scheduling run: the best schedule found, its score,
/// the target dates that were assigned, and how many local-search steps were
/// spent (useful for diagnostics / the UI).
/// </summary>
/// <param name="Schedule">The chosen schedule.</param>
/// <param name="Evaluation">Its KPIs and penalty.</param>
/// <param name="DueByJob">The assigned target date per job, in seconds.</param>
/// <param name="LocalSearchSteps">Neighbours evaluated during the polish phase.</param>
public sealed record SchedulingResult(
    Schedule Schedule,
    ScheduleEvaluation Evaluation,
    IReadOnlyDictionary<int, long> DueByJob,
    int LocalSearchSteps);
