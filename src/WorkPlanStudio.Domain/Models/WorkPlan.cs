using System.ComponentModel.DataAnnotations.Schema;

namespace WorkPlanStudio.Models;

public class WorkPlan
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "local";
    public string PlanNumber { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string PartName { get; set; } = "";
    public string? Revision { get; set; }
    public WorkPlanStatus Status { get; set; } = WorkPlanStatus.Draft;
    public int LotSize { get; set; } = 1;
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public long Version { get; set; }
    public List<Operation> Operations { get; set; } = new();

    [NotMapped] public int OperationCount => Operations.Count;
    [NotMapped] public decimal TotalSetupMinutes => Operations.Sum(o => o.SetupTimeMinutes);
    [NotMapped] public decimal TotalUnitMinutes => Operations.Sum(o => o.TimePerPieceMinutes);
    [NotMapped] public decimal TotalTimeMinutes => Operations.Sum(o => o.TotalTimeMinutes(LotSize));
    [NotMapped] public decimal TotalCost => Operations.Sum(o => o.Cost(LotSize));
}
