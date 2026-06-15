namespace WorkPlanStudio.Services;

/// <summary>The generated production schedule plus summary figures for the UI.</summary>
public sealed record SchedulingResult
{
    public DateTime StartAt { get; init; }
    public DateTime EndAt { get; init; }
    public IReadOnlyList<ScheduledOperation> Items { get; init; } = [];
    public IReadOnlyList<WorkCenterScheduleSummary> WorkCenters { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public decimal TotalBookedMinutes => Items.Sum(i => i.DurationMinutes);
    public int ScheduledPlans => Items.Select(i => i.WorkPlanId).Distinct().Count();
    public int ScheduledOperations => Items.Count;
    public TimeSpan CalendarSpan => EndAt > StartAt ? EndAt - StartAt : TimeSpan.Zero;
}
