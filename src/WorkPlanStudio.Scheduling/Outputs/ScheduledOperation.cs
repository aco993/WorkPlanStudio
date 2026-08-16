namespace WorkPlanStudio.Scheduling;

/// <summary>
/// One operation placed on the timeline: which job/step it belongs to, the work
/// center and parallel slot it occupies, and its start/end in seconds from the
/// horizon. This is the unit a Gantt bar is drawn from.
/// </summary>
/// <param name="JobId">Owning job.</param>
/// <param name="StepNumber">Step within the job.</param>
/// <param name="WorkCenterId">Work center the step runs on.</param>
/// <param name="SlotIndex">Which parallel slot of the work center (0-based).</param>
/// <param name="StartSeconds">Start time in seconds from the horizon.</param>
/// <param name="EndSeconds">End time in seconds from the horizon.</param>
public sealed record ScheduledOperation(
    int JobId,
    int StepNumber,
    int WorkCenterId,
    int SlotIndex,
    long StartSeconds,
    long EndSeconds)
{
    /// <summary>
    /// Change-over time included in this placement, in seconds. The bar on the
    /// Gantt chart covers setup plus processing; this says how much of it was
    /// setup.
    /// </summary>
    public long SetupSeconds { get; init; }

    /// <summary>Total occupied time, setup included.</summary>
    public long DurationSeconds => EndSeconds - StartSeconds;

    /// <summary>Time spent actually processing, excluding change-over.</summary>
    public long ProcessingSeconds => DurationSeconds - SetupSeconds;
}
