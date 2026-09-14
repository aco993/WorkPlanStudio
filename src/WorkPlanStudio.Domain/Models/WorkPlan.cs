using System.ComponentModel.DataAnnotations.Schema;

namespace WorkPlanStudio.Models;

/// <summary>
/// A work plan (routing): the ordered list of operations needed to manufacture
/// a part, together with header data and a default lot size used for costing.
/// </summary>
public class WorkPlan
{
    public int Id { get; set; }

    /// <summary>Unique plan identifier, e.g. "WP-1001".</summary>
    public string PlanNumber { get; set; } = "";

    public string PartNumber { get; set; } = "";
    public string PartName { get; set; } = "";
    public string? Revision { get; set; }

    public WorkPlanStatus Status { get; set; } = WorkPlanStatus.Draft;

    /// <summary>Default batch quantity used for time and cost calculations.</summary>
    public int LotSize { get; set; } = 1;

    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }

    public List<Operation> Operations { get; set; } = new();

    [NotMapped]
    public int OperationCount => Operations.Count;

    [NotMapped]
    public decimal TotalSetupMinutes => Operations.Sum(o => o.SetupTimeMinutes);

    [NotMapped]
    public decimal TotalUnitMinutes => Operations.Sum(o => o.TimePerPieceMinutes);

    /// <summary>Total throughput time for the whole lot, across all operations.</summary>
    [NotMapped]
    public decimal TotalTimeMinutes => Operations.Sum(o => o.TotalTimeMinutes(LotSize));

    /// <summary>Estimated manufacturing cost for the whole lot.</summary>
    [NotMapped]
    public decimal TotalCost => Operations.Sum(o => o.Cost(LotSize));
}
