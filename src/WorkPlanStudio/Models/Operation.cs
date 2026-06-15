namespace WorkPlanStudio.Models;

/// <summary>
/// A single step within a work plan ("Arbeitsgang"): what is done, where, and
/// how long it takes. Times are split into a one-off setup time and a per-piece
/// run time, which is the standard way routings are costed in manufacturing.
/// </summary>
public class Operation
{
    public int Id { get; set; }

    public int WorkPlanId { get; set; }
    public WorkPlan? WorkPlan { get; set; }

    /// <summary>Sequence number within the plan (10, 20, 30 …).</summary>
    public int OperationNumber { get; set; }

    public string Description { get; set; } = "";

    public int WorkCenterId { get; set; }
    public WorkCenter? WorkCenter { get; set; }

    /// <summary>One-off setup / changeover time in minutes.</summary>
    public decimal SetupTimeMinutes { get; set; }

    /// <summary>Run time per piece in minutes.</summary>
    public decimal TimePerPieceMinutes { get; set; }

    public string? Remarks { get; set; }

    /// <summary>Total time in minutes to run this operation for a whole lot.</summary>
    public decimal TotalTimeMinutes(int lotSize) => SetupTimeMinutes + TimePerPieceMinutes * lotSize;

    /// <summary>Estimated cost of this operation for a lot, using the work-center rate.</summary>
    public decimal Cost(int lotSize) =>
        WorkCenter is null ? 0m : TotalTimeMinutes(lotSize) / 60m * WorkCenter.HourlyRate;
}
