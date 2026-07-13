using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

public enum ApplicationResultStatus
{
    Success,
    ValidationFailed,
    NotFound,
    Conflict,
    PersistenceFailed,
    Cancelled
}

/// <summary>A small typed result for mutation boundaries; this is not a generic result framework.</summary>
public sealed record ApplicationResult<T>(
    ApplicationResultStatus Status,
    T? Value = default,
    IReadOnlyList<ValidationIssue>? ValidationIssues = null)
{
    public bool IsSuccess => Status == ApplicationResultStatus.Success;

    public static ApplicationResult<T> Success(T value) => new(ApplicationResultStatus.Success, value);

    public static ApplicationResult<T> Validation(IReadOnlyList<ValidationIssue> issues) =>
        new(ApplicationResultStatus.ValidationFailed, default, issues);

    public static ApplicationResult<T> Conflict(params ValidationIssue[] issues) =>
        new(ApplicationResultStatus.Conflict, default, issues);

    public static ApplicationResult<T> NotFound() => new(ApplicationResultStatus.NotFound);

    public static ApplicationResult<T> PersistenceFailed() => new(ApplicationResultStatus.PersistenceFailed);
}
