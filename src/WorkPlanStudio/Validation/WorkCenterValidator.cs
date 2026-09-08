using WorkPlanStudio.Models;

namespace WorkPlanStudio.Validation;

/// <summary>Business rules shared by every work-center entry point.</summary>
public static class WorkCenterValidator
{
    public const int MinCapacity = 1;
    public const int MaxCapacity = 64;
    public const decimal MaxHourlyRate = 1_000_000m;

    public static IReadOnlyList<ValidationIssue> Validate(WorkCenter center)
    {
        var issues = new List<ValidationIssue>();
        RequiredLength(issues, nameof(center.Code), center.Code, 20);
        RequiredLength(issues, nameof(center.Name), center.Name, 100);

        if (center.CostCenter?.Trim().Length > 20)
            issues.Add(new(nameof(center.CostCenter), "Val_MaxLength", 20));
        if (center.HourlyRate < 0 || center.HourlyRate > MaxHourlyRate)
            issues.Add(new(nameof(center.HourlyRate), "Val_HourlyRateRange", 0, MaxHourlyRate));
        if (center.ParallelCapacity < MinCapacity || center.ParallelCapacity > MaxCapacity)
            issues.Add(new(nameof(center.ParallelCapacity), "Val_CapacityRange", MinCapacity, MaxCapacity));
        if (WorkingTime.ShiftPatterns.ByKey(center.ShiftPatternKey) is null)
            issues.Add(new(nameof(center.ShiftPatternKey), "Val_ShiftPatternUnknown"));

        return issues;
    }

    public const int MaxAbsenceLabelLength = 80;

    /// <summary>Business rules for a work-center absence.</summary>
    public static IReadOnlyList<ValidationIssue> ValidateAbsence(WorkCenterAbsence absence)
    {
        ArgumentNullException.ThrowIfNull(absence);
        var issues = new List<ValidationIssue>();

        if (absence.WorkCenterId <= 0)
            issues.Add(new(nameof(absence.WorkCenterId), "Val_Required"));
        if (absence.End <= absence.Start)
            issues.Add(new(nameof(absence.End), "Val_AbsenceEndBeforeStart"));
        else if (absence.End - absence.Start > TimeSpan.FromDays(366))
            issues.Add(new(nameof(absence.End), "Val_AbsenceTooLong"));
        if (absence.Label?.Trim().Length > MaxAbsenceLabelLength)
            issues.Add(new(nameof(absence.Label), "Val_MaxLength", MaxAbsenceLabelLength));
        if (!Enum.IsDefined(absence.Kind))
            issues.Add(new(nameof(absence.Kind), "Val_Required"));

        return issues;
    }

    private static void RequiredLength(
        ICollection<ValidationIssue> issues,
        string field,
        string? value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new(field, "Val_Required"));
        else if (value.Trim().Length > maxLength)
            issues.Add(new(field, "Val_MaxLength", maxLength));
    }
}
