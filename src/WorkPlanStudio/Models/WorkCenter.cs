namespace WorkPlanStudio.Models;

/// <summary>
/// A place where work is performed — a machine, cell or manual station.
/// Operations are booked against a work center, which carries the hourly rate
/// used for cost estimation.
/// </summary>
public class WorkCenter
{
    public int Id { get; set; }

    /// <summary>Short identifier, e.g. "CNC-300".</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Accounting cost center this work center belongs to.</summary>
    public string CostCenter { get; set; } = "";

    /// <summary>Machine-hour rate used to estimate operation cost.</summary>
    public decimal HourlyRate { get; set; }

    /// <summary>Number of jobs this center can process concurrently.</summary>
    public int ParallelCapacity { get; set; } = 1;

    /// <summary>
    /// Key of the <see cref="WorkingTime.ShiftPatterns"/> preset this center is
    /// staffed by. "continuous" means an unattended machine with no working-time
    /// constraint at all.
    /// </summary>
    public string ShiftPatternKey { get; set; } = WorkingTime.ShiftPatterns.Continuous.Key;

    public bool IsActive { get; set; } = true;

    public ICollection<Operation> Operations { get; set; } = new List<Operation>();

    public ICollection<WorkCenterAbsence> Absences { get; set; } = new List<WorkCenterAbsence>();
}
