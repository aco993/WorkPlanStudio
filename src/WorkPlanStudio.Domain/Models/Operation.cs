namespace WorkPlanStudio.Models;

public class Operation
{
    public int Id { get; set; }
    public int WorkPlanId { get; set; }
    public WorkPlan? WorkPlan { get; set; }
    public int OperationNumber { get; set; }
    public string Description { get; set; } = "";
    public int WorkCenterId { get; set; }
    public WorkCenter? WorkCenter { get; set; }
    public decimal SetupTimeMinutes { get; set; }
    public decimal TimePerPieceMinutes { get; set; }
    public string SetupFamily { get; set; } = "DEFAULT";
    public string? Remarks { get; set; }

    public decimal TotalTimeMinutes(int lotSize) => SetupTimeMinutes + TimePerPieceMinutes * lotSize;
    public decimal Cost(int lotSize) =>
        WorkCenter is null ? 0m : TotalTimeMinutes(lotSize) / 60m * WorkCenter.HourlyRate;
}
