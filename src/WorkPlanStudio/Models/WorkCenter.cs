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

    public bool IsActive { get; set; } = true;

    public ICollection<Operation> Operations { get; set; } = new List<Operation>();
}
