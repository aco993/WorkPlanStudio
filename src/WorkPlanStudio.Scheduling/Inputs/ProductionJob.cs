namespace WorkPlanStudio.Scheduling;

/// <summary>
/// A job to be scheduled: an ordered chain of <see cref="JobStep"/>s (a released
/// routing) that flows through work centers. All times are integer seconds
/// measured from the planning horizon (second 0).
/// </summary>
public sealed class ProductionJob
{
    /// <summary>Stable identifier of the job (e.g. the work-plan id).</summary>
    public required int Id { get; init; }

    /// <summary>Human-readable label shown in the UI (e.g. the plan number).</summary>
    public required string Reference { get; init; }

    /// <summary>Earliest second at which the first step may start. Defaults to 0.</summary>
    public long ReleaseSeconds { get; init; }

    /// <summary>
    /// Relative importance, used by the weighted dispatch rule (WSPT). Larger =
    /// more important. Defaults to 1.
    /// </summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>
    /// Caller-supplied target date in seconds, honoured by
    /// <see cref="DueDateRule.Explicit"/>. When null, the explicit rule falls
    /// back to a constant allowance.
    /// </summary>
    public long? ExplicitDueSeconds { get; init; }

    /// <summary>The steps in execution order (strictly increasing step numbers).</summary>
    public required IReadOnlyList<JobStep> Steps { get; init; }

    /// <summary>Sum of every step's processing time, in seconds.</summary>
    public long TotalProcessingSeconds => Steps.Sum(s => s.DurationSeconds);

    /// <summary>Number of operations in the routing.</summary>
    public int StepCount => Steps.Count;
}
