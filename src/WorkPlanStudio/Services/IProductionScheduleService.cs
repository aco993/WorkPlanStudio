using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// Generates a production schedule for the released work plans. The Scheduling
/// page depends on this abstraction (not the concrete service) so it can be
/// component-tested with a lightweight fake — no database, no engine run.
/// </summary>
public interface IProductionScheduleService
{
    /// <summary>The Gantt's display day when a caller does not name one: eight hours.</summary>
    /// <remarks>
    /// This constant used to sit on <c>SchedulingParameters</c>, where it was the
    /// one piece of rendering in a library that advertises having no UI concerns.
    /// It is an app-side default now, overridable per run.
    /// </remarks>
    public const int DefaultMinutesPerWorkingDay = 480;

    /// <summary>Builds a schedule for the released orders using the given parameters.</summary>
    Task<ScheduleResult> GenerateAsync(SchedulingParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same, reporting how far the search has got and letting the caller name
    /// the Gantt's display day.
    /// </summary>
    /// <param name="parameters">The engine parameters.</param>
    /// <param name="minutesPerWorkingDay">Working minutes per calendar day, used only to render the chart.</param>
    /// <param name="progress">Reported at every multi-start boundary; may be <c>null</c>.</param>
    /// <param name="cancellationToken">
    /// Honoured between descents, so on a single-threaded host it is something a
    /// Cancel button can actually signal rather than a parameter that is never read.
    /// </param>
    Task<ScheduleResult> GenerateAsync(
        SchedulingParameters parameters,
        int minutesPerWorkingDay,
        IProgress<ScheduleRunProgress>? progress,
        CancellationToken cancellationToken);
}
