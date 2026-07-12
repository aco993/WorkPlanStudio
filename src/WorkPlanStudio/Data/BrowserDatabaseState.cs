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
    ResetFailed
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
