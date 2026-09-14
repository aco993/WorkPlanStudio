using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

public enum ApplicationResultStatus
{
    Success,
    ValidationFailed,
    NotFound,
    Conflict,
    PersistenceFailed,

    /// <summary>
    /// The change was undone because the browser has no room for the snapshot.
    /// A separate status from <see cref="PersistenceFailed"/> because it is the
    /// one storage failure the user can do something about, and the storage
    /// layer has always known the difference - it was only thrown away here.
    /// </summary>
    StorageFull,

    /// <summary>The current persona lacks the policy this action requires.</summary>
    Forbidden
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

    /// <summary>The snapshot did not fit; the database was put back the way it was.</summary>
    public static ApplicationResult<T> StorageFull() => new(ApplicationResultStatus.StorageFull);

    public static ApplicationResult<T> Forbidden() => new(ApplicationResultStatus.Forbidden);
}
