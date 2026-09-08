using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Models;

/// <summary>
/// A one-off closed period for a work center — leave, maintenance, a breakdown —
/// on top of its repeating shift pattern. Stored as plant-local wall-clock time.
/// </summary>
public class WorkCenterAbsence
{
    public int Id { get; set; }

    public int WorkCenterId { get; set; }

    public WorkCenter? WorkCenter { get; set; }

    /// <summary>First closed moment.</summary>
    public DateTime Start { get; set; }

    /// <summary>First moment work may resume.</summary>
    public DateTime End { get; set; }

    public AbsenceKind Kind { get; set; } = AbsenceKind.Maintenance;

    /// <summary>Shown on the Gantt chart, e.g. "Spindle service".</summary>
    public string Label { get; set; } = "";

    /// <summary>The working-time model's view of this row.</summary>
    public AbsencePeriod ToPeriod() => new(Start, End, Kind, Label);
}
