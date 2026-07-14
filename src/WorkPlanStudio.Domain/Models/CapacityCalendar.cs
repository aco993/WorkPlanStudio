namespace WorkPlanStudio.Models;

public sealed class CalendarShift
{
    public int Id { get; set; }
    public int WorkCenterId { get; set; }
    public WorkCenter? WorkCenter { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
}

public sealed class MachineDowntime
{
    public int Id { get; set; }
    public int WorkCenterId { get; set; }
    public WorkCenter? WorkCenter { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class SetupTransition
{
    public int Id { get; set; }
    public int WorkCenterId { get; set; }
    public WorkCenter? WorkCenter { get; set; }
    public string FromFamily { get; set; } = "DEFAULT";
    public string ToFamily { get; set; } = "DEFAULT";
    public int DurationMinutes { get; set; }
}
