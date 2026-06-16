using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// Generates a production schedule for the released work plans. The Scheduling
/// page depends on this abstraction (not the concrete service) so it can be
/// component-tested with a lightweight fake — no database, no engine run.
/// </summary>
public interface IProductionScheduleService
{
    /// <summary>Builds a schedule for the released plans using the given parameters.</summary>
    Task<ScheduleResult> GenerateAsync(SchedulingParameters parameters);
}
