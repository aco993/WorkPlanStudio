namespace WorkPlanStudio.Data;

public enum BrowserDatabaseFailure
{
    None,
    InvalidBase64,
    InvalidSqlite,
    TruncatedPayload,
    UnsupportedSchema,
    ReadFailed,
    WriteFailed,
    ExportFailed,
    ResetFailed,

    /// <summary>
    /// The origin's storage budget is full. Separate from <see cref="WriteFailed"/>
    /// because it is the one write failure the visitor can do something about.
    /// </summary>
    QuotaExceeded,

    /// <summary>A stored payload one or more versions old could not be upgraded to the current schema.</summary>
    UpgradeFailed,

    /// <summary>The current persona may not perform this operation.</summary>
    Forbidden
}

public sealed record BrowserDatabaseReadiness(
    bool IsReady,
    BrowserDatabaseFailure Failure = BrowserDatabaseFailure.None,
    int? StoredVersion = null)
{
    public static BrowserDatabaseReadiness Ready { get; } = new(true);
}

public sealed record BrowserStorageResult(bool IsSuccess, BrowserDatabaseFailure Failure = BrowserDatabaseFailure.None)
{
    public static BrowserStorageResult Success { get; } = new(true);
}

public sealed record BrowserDatabaseOptions(string DatabasePath, int SchemaVersion);

public sealed class BrowserDatabaseUnavailableException : InvalidOperationException
{
    public BrowserDatabaseUnavailableException(BrowserDatabaseReadiness readiness)
        : base($"Browser database requires recovery: {readiness.Failure}.") => Readiness = readiness;

    public BrowserDatabaseReadiness Readiness { get; }
}
