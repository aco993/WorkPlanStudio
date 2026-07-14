namespace WorkPlanStudio.Models;

public sealed class AuditEntry
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? ChangesJson { get; set; }
    public string CorrelationId { get; set; } = "";
    public DateTime OccurredUtc { get; set; }
}
