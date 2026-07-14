namespace WorkPlanStudio.Models;

public enum ProductionOrderStatus
{
    Draft = 0,
    Released = 1,
    Scheduled = 2,
    InProgress = 3,
    Completed = 4,
    Cancelled = 5
}

public sealed class ProductionOrder
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public int WorkPlanId { get; set; }
    public WorkPlan? WorkPlan { get; set; }
    public int Quantity { get; set; } = 1;
    public DateTime ReleaseUtc { get; set; }
    public DateTime DueUtc { get; set; }
    public int Priority { get; set; } = 5;
    public ProductionOrderStatus Status { get; set; } = ProductionOrderStatus.Draft;
    public string RoutingRevision { get; set; } = "";
    public string RoutingSnapshotJson { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public long Version { get; set; }
}
