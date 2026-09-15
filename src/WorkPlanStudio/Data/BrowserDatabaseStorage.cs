using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace WorkPlanStudio.Data;

/// <param name="Data">The Base64 SQLite file.</param>
/// <param name="Version">The schema version the payload was written at.</param>
/// <param name="Revision">
/// Counts writes, not schema changes. Every tab of this app holds the whole
/// database in memory and writes the whole of it back, so without a revision the
/// last writer wins and the loser is never told: two tabs quietly deleted each
/// other's <em>saved</em> work. A write now carries the revision it was based on
/// and is refused if storage has moved on.
/// </param>
public sealed record StoredDatabase(string Data, int Version, long Revision = 0);

/// <summary>How a write to the browser's storage ended.</summary>
public enum StorageWriteOutcome
{
    Saved,

    /// <summary>The origin's storage budget is full. Distinct because the user can act on it.</summary>
    QuotaExceeded,

    /// <summary>
    /// Another tab wrote after this one loaded. The write was refused rather than
    /// applied, so the other tab's work is still there.
    /// </summary>
    ChangedElsewhere,

    /// <summary>Anything else the browser refused.</summary>
    Failed
}

/// <param name="Outcome">What happened.</param>
/// <param name="Detail">The browser's own message, for the log — never shown raw.</param>
/// <param name="Revision">The revision now in storage, after a successful write.</param>
public sealed record StorageWriteResult(StorageWriteOutcome Outcome, string? Detail = null, long Revision = 0)
{
    public static StorageWriteResult Saved { get; } = new(StorageWriteOutcome.Saved);

    public bool IsSuccess => Outcome == StorageWriteOutcome.Saved;
}

/// <summary>The shape <c>workplanDb.save</c> reports back, so a full quota is a result and not an exception.</summary>
public sealed record StorageWriteReport(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("revision")] long Revision = 0);

public interface IBrowserDatabaseStorage
{
    ValueTask<StoredDatabase?> LoadAsync(CancellationToken cancellationToken = default);

    /// <param name="database">What to store.</param>
    /// <param name="expectedRevision">
    /// The revision this write is based on. Storage compares it against what it
    /// holds and refuses the write if another tab got there first;
    /// <c>-1</c> writes unconditionally, which is what replacing the whole
    /// database (import, reset) means.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<StorageWriteResult> SaveAsync(
        StoredDatabase database, long expectedRevision, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    ValueTask ExportAsync(StoredDatabase database, CancellationToken cancellationToken = default);

    /// <summary>Asks the visitor for an exported payload file. <c>null</c> when they cancel.</summary>
    ValueTask<StoredDatabase?> PickImportAsync(CancellationToken cancellationToken = default);
}

/// <summary>The only JavaScript boundary for the persisted browser database payload.</summary>
public sealed class JsBrowserDatabaseStorage : IBrowserDatabaseStorage
{
    private readonly IJSRuntime _js;

    public JsBrowserDatabaseStorage(IJSRuntime js) => _js = js;

    public ValueTask<StoredDatabase?> LoadAsync(CancellationToken cancellationToken = default) =>
        _js.InvokeAsync<StoredDatabase?>("workplanDb.load", cancellationToken);

    public async ValueTask<StorageWriteResult> SaveAsync(
        StoredDatabase database, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var report = await _js.InvokeAsync<StorageWriteReport>(
            "workplanDb.save", cancellationToken, database.Data, database.Version, expectedRevision);

        if (report.Ok)
            return new StorageWriteResult(StorageWriteOutcome.Saved, Revision: report.Revision);

        return new StorageWriteResult(
            report.Reason switch
            {
                "quota" => StorageWriteOutcome.QuotaExceeded,
                "stale" => StorageWriteOutcome.ChangedElsewhere,
                _ => StorageWriteOutcome.Failed
            },
            report.Message);
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
        _js.InvokeVoidAsync("workplanDb.clear", cancellationToken);

    public ValueTask ExportAsync(StoredDatabase database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        return _js.InvokeVoidAsync("workplanDb.export", cancellationToken, database.Data, database.Version);
    }

    public ValueTask<StoredDatabase?> PickImportAsync(CancellationToken cancellationToken = default) =>
        _js.InvokeAsync<StoredDatabase?>("workplanDb.pickImport", cancellationToken);
}
