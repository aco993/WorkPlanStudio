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
