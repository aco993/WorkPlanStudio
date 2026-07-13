namespace WorkPlanStudio.Services;

/// <summary>Stable reason codes for a work plan rejected at the scheduling boundary.</summary>
public enum SchedulePreparationErrorCode
{
    InvalidPlan,
    InvalidLotSize,
    NoOperations,
    InvalidOperationNumber,
    DuplicateOperationNumber,
    InvalidOperationDuration,
    MissingWorkCenter,
    InactiveWorkCenter,
    InvalidWorkCenterCapacity
}

/// <summary>A structured mapper diagnostic; UI text is localized from <see cref="Code"/>.</summary>
public sealed record SchedulePreparationIssue(
    int PlanId,
    string PlanReference,
    int? OperationNumber,
    SchedulePreparationErrorCode Code,
    string? WorkCenterReference = null);

/// <summary>All-or-nothing preparation outcome for each released plan.</summary>
public sealed record SchedulePreparationResult(
    ScheduleMapper.Input? Input,
    IReadOnlyList<SchedulePreparationIssue> Errors)
{
    public bool HasRejectedPlans => Errors.Count > 0;

    public IReadOnlyList<int> RejectedPlanIds => Errors
        .Select(issue => issue.PlanId)
        .Distinct()
        .ToList();
}
