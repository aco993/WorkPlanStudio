using System.Text.Json.Serialization;
using Microsoft.JSInterop;

namespace WorkPlanStudio.Data;

public sealed record StoredDatabase(string Data, int Version);

/// <summary>How a write to the browser's storage ended.</summary>
public enum StorageWriteOutcome
{
    Saved,

    /// <summary>The origin's storage budget is full. Distinct because the user can act on it.</summary>
    QuotaExceeded,

    /// <summary>Anything else the browser refused.</summary>
    Failed
}

/// <param name="Outcome">What happened.</param>
/// <param name="Detail">The browser's own message, for the log — never shown raw.</param>
public sealed record StorageWriteResult(StorageWriteOutcome Outcome, string? Detail = null)
{
    public static StorageWriteResult Saved { get; } = new(StorageWriteOutcome.Saved);

    public bool IsSuccess => Outcome == StorageWriteOutcome.Saved;
}

/// <summary>The shape <c>workplanDb.save</c> reports back, so a full quota is a result and not an exception.</summary>
public sealed record StorageWriteReport(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("message")] string? Message = null);

public interface IBrowserDatabaseStorage
{
    ValueTask<StoredDatabase?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask<StorageWriteResult> SaveAsync(StoredDatabase database, CancellationToken cancellationToken = default);

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

    public async ValueTask<StorageWriteResult> SaveAsync(StoredDatabase database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var report = await _js.InvokeAsync<StorageWriteReport>(
            "workplanDb.save", cancellationToken, database.Data, database.Version);

        if (report.Ok)
            return StorageWriteResult.Saved;

        return new StorageWriteResult(
            report.Reason == "quota" ? StorageWriteOutcome.QuotaExceeded : StorageWriteOutcome.Failed,
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
