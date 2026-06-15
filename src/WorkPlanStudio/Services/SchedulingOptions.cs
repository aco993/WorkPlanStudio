namespace WorkPlanStudio.Services;

/// <summary>Inputs that describe how the finite-capacity schedule should be built.</summary>
public sealed record SchedulingOptions
{
    public DateTime StartAt { get; init; } = DateTime.Today.AddHours(8);
    public TimeSpan WorkDayStart { get; init; } = TimeSpan.FromHours(8);
    public TimeSpan WorkDayEnd { get; init; } = TimeSpan.FromHours(16);
    public bool IncludeDrafts { get; init; }
    public bool ExcludeWeekends { get; init; } = true;
}
