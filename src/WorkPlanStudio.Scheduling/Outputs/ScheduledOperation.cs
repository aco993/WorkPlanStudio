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
    /// <summary>Processing time of this placement, in seconds.</summary>
    public long DurationSeconds => EndSeconds - StartSeconds;
}
