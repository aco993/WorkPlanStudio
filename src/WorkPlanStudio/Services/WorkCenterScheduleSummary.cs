namespace WorkPlanStudio.Services;

/// <summary>Aggregated load for one work center in a generated schedule.</summary>
public sealed record WorkCenterScheduleSummary
{
    public int WorkCenterId { get; init; }
    public string WorkCenterCode { get; init; } = "";
    public string WorkCenterName { get; init; } = "";
    public decimal BookedMinutes { get; init; }
    public DateTime FirstStartAt { get; init; }
    public DateTime LastEndAt { get; init; }
    public double UtilizationPercent { get; init; }
}
