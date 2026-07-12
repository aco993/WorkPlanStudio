using WorkPlanStudio.Models;

namespace WorkPlanStudio.Validation;

/// <summary>Business rules shared by every work-plan entry point.</summary>
public static class WorkPlanValidator
{
    public const int MaxLotSize = 1_000_000;
    public const int MaxOperationNumber = 1_000_000;
    public const decimal MaxOperationMinutes = 1_000_000m;

    public static IReadOnlyList<ValidationIssue> Validate(
        WorkPlan plan,
        IReadOnlyDictionary<int, WorkCenter> centers,
        WorkPlanStatus? previousStatus = null)
    {
        var issues = new List<ValidationIssue>();

        ValidateRequiredLength(issues, nameof(plan.PlanNumber), plan.PlanNumber, 20);
        ValidateOptionalLength(issues, nameof(plan.PartNumber), plan.PartNumber, 40);
        ValidateRequiredLength(issues, nameof(plan.PartName), plan.PartName, 120);
        ValidateOptionalLength(issues, nameof(plan.Revision), plan.Revision, 10);

        if (!Enum.IsDefined(plan.Status))
            issues.Add(new(nameof(plan.Status), "Val_StatusInvalid"));
        else if (previousStatus == WorkPlanStatus.Archived && plan.Status == WorkPlanStatus.Released)
            issues.Add(new(nameof(plan.Status), "Val_StatusTransition"));

        if (plan.LotSize <= 0 || plan.LotSize > MaxLotSize)
            issues.Add(new(nameof(plan.LotSize), "Val_LotSizeRange", 1, MaxLotSize));

        if (plan.Operations.Count == 0)
            issues.Add(new(nameof(plan.Operations), "Val_NeedOperation"));

        var duplicateNumbers = plan.Operations
            .GroupBy(operation => operation.OperationNumber)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        foreach (var operation in plan.Operations)
        {
            var prefix = $"Operations[{operation.OperationNumber}]";
            if (operation.OperationNumber <= 0 || operation.OperationNumber > MaxOperationNumber)
                issues.Add(new(prefix, "Val_OperationNumberRange", 1, MaxOperationNumber));
            if (duplicateNumbers.Contains(operation.OperationNumber))
                issues.Add(new(prefix, "Val_OperationNumberDuplicate", operation.OperationNumber));

            ValidateRequiredLength(issues, prefix, operation.Description, 120);
            ValidateOptionalLength(issues, prefix, operation.Remarks, 250);

            if (operation.SetupTimeMinutes < 0 || operation.SetupTimeMinutes > MaxOperationMinutes)
                issues.Add(new(prefix, "Val_SetupTimeRange", 0, MaxOperationMinutes));
            if (operation.TimePerPieceMinutes < 0 || operation.TimePerPieceMinutes > MaxOperationMinutes)
                issues.Add(new(prefix, "Val_RunTimeRange", 0, MaxOperationMinutes));

            if (!centers.TryGetValue(operation.WorkCenterId, out var center))
                issues.Add(new(prefix, "Val_WorkCenterMissing", operation.WorkCenterId));
            else if (plan.Status == WorkPlanStatus.Released && !center.IsActive)
                issues.Add(new(prefix, "Val_WorkCenterInactive", center.Code));

            if (plan.LotSize > 0 && operation.SetupTimeMinutes >= 0 && operation.TimePerPieceMinutes >= 0)
            {
                try
                {
                    _ = Services.ScheduleMapper.ToSeconds(
                        operation.SetupTimeMinutes,
                        operation.TimePerPieceMinutes,
                        plan.LotSize);
                }
                catch (OverflowException)
                {
                    issues.Add(new(prefix, "Val_OperationDurationOverflow"));
                }
            }
        }

        return issues.Distinct().ToList();
    }

    private static void ValidateRequiredLength(
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

    private static void ValidateOptionalLength(
        ICollection<ValidationIssue> issues,
        string field,
        string? value,
        int maxLength)
    {
        if (value?.Trim().Length > maxLength)
            issues.Add(new(field, "Val_MaxLength", maxLength));
    }
}
