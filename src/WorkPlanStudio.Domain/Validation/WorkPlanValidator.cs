using WorkPlanStudio.Models;

namespace WorkPlanStudio.Validation;

/// <summary>Business rules shared by every work-plan entry point.</summary>
public static class WorkPlanValidator
{
    public const int MaxLotSize = 1_000_000;
    public const int MaxOperationNumber = 1_000_000;
    public const decimal MaxOperationMinutes = 1_000_000m;

    /// <summary>Decimal places an operation time may carry; the column stores exactly this.</summary>
    public const int MinutesScale = 2;

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
            // "Operations[10].Description" rather than "Operations[10]": a form with
            // sixty cells in it can only mark the wrong one if it is told which one,
            // and a summary line can only name the column if the column is in the
            // name. Callers that want the whole table read the part before the '['.
            var prefix = $"Operations[{operation.OperationNumber}]";
            if (operation.OperationNumber <= 0 || operation.OperationNumber > MaxOperationNumber)
                issues.Add(new($"{prefix}.{nameof(operation.OperationNumber)}", "Val_OperationNumberRange", 1, MaxOperationNumber));
            if (duplicateNumbers.Contains(operation.OperationNumber))
                issues.Add(new($"{prefix}.{nameof(operation.OperationNumber)}", "Val_OperationNumberDuplicate", operation.OperationNumber));

            ValidateRequiredLength(issues, $"{prefix}.{nameof(operation.Description)}", operation.Description, 120);
            ValidateOptionalLength(issues, $"{prefix}.{nameof(operation.Remarks)}", operation.Remarks, 250);

            if (operation.SetupTimeMinutes < 0 || operation.SetupTimeMinutes > MaxOperationMinutes)
                issues.Add(new($"{prefix}.{nameof(operation.SetupTimeMinutes)}", "Val_SetupTimeRange", 0, MaxOperationMinutes));
            else if (Text.ExceedsScale(operation.SetupTimeMinutes, MinutesScale))
                issues.Add(new($"{prefix}.{nameof(operation.SetupTimeMinutes)}", "Val_Scale", MinutesScale));
            if (operation.TimePerPieceMinutes < 0 || operation.TimePerPieceMinutes > MaxOperationMinutes)
                issues.Add(new($"{prefix}.{nameof(operation.TimePerPieceMinutes)}", "Val_RunTimeRange", 0, MaxOperationMinutes));
            else if (Text.ExceedsScale(operation.TimePerPieceMinutes, MinutesScale))
                issues.Add(new($"{prefix}.{nameof(operation.TimePerPieceMinutes)}", "Val_Scale", MinutesScale));

            if (!centers.TryGetValue(operation.WorkCenterId, out var center))
                issues.Add(new($"{prefix}.{nameof(operation.WorkCenterId)}", "Val_WorkCenterMissing", operation.WorkCenterId));
            else if (plan.Status == WorkPlanStatus.Released && !center.IsActive)
                issues.Add(new($"{prefix}.{nameof(operation.WorkCenterId)}", "Val_WorkCenterInactive", center.Code));

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
                    // The product overflows, so it belongs to the run time rather
                    // than to any one cell; that is the column a reader can change.
                    issues.Add(new($"{prefix}.{nameof(operation.TimePerPieceMinutes)}", "Val_OperationDurationOverflow"));
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
        else if (Text.HasControlCharacters(value))
            issues.Add(new(field, "Val_SingleLine"));
    }

    private static void ValidateOptionalLength(
        ICollection<ValidationIssue> issues,
        string field,
        string? value,
        int maxLength)
    {
        if (value?.Trim().Length > maxLength)
            issues.Add(new(field, "Val_MaxLength", maxLength));
        else if (Text.HasControlCharacters(value))
            issues.Add(new(field, "Val_SingleLine"));
    }
}
