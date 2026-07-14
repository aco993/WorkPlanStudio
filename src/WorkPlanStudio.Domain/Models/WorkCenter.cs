namespace WorkPlanStudio.Models;

public class WorkCenter
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = "local";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string CostCenter { get; set; } = "";
    public decimal HourlyRate { get; set; }
    public int ParallelCapacity { get; set; } = 1;
    public string TimeZoneId { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }
    public ICollection<Operation> Operations { get; set; } = new List<Operation>();
    public ICollection<CalendarShift> CalendarShifts { get; set; } = new List<CalendarShift>();
    public ICollection<MachineDowntime> Downtimes { get; set; } = new List<MachineDowntime>();
    public ICollection<SetupTransition> SetupTransitions { get; set; } = new List<SetupTransition>();
}
