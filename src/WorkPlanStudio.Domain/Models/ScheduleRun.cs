namespace WorkPlanStudio.Models;

public enum ScheduleRunStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4
}

public sealed class ScheduleRun
{
    public Guid Id { get; set; }
    public string OwnerId { get; set; } = "";
    public ScheduleRunStatus Status { get; set; }
    public int ProgressPercent { get; set; }
    public string ParametersJson { get; set; } = "";
    public string? ResultJson { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime? CancellationRequestedUtc { get; set; }
    public int AttemptCount { get; set; }
    public long Version { get; set; }
}
