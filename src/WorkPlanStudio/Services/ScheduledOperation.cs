namespace WorkPlanStudio.Services;

/// <summary>A single operation placed on a work-center timeline.</summary>
public sealed record ScheduledOperation
{
    public int WorkPlanId { get; init; }
    public string PlanNumber { get; init; } = "";
    public string PartNumber { get; init; } = "";
    public string PartName { get; init; } = "";
    public int OperationId { get; init; }
    public int OperationNumber { get; init; }
    public string Description { get; init; } = "";
    public int WorkCenterId { get; init; }
    public string WorkCenterCode { get; init; } = "";
    public string WorkCenterName { get; init; } = "";
    public DateTime StartAt { get; init; }
    public DateTime EndAt { get; init; }
    public decimal DurationMinutes { get; init; }
    public decimal Cost { get; init; }
    public double QueueDelayMinutes { get; init; }
}
