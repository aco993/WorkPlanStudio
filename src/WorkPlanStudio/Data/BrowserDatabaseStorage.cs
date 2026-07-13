using Microsoft.JSInterop;

namespace WorkPlanStudio.Data;

public sealed record StoredDatabase(string Data, int Version);

public interface IBrowserDatabaseStorage
{
    ValueTask<StoredDatabase?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(StoredDatabase database, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    ValueTask ExportAsync(StoredDatabase database, CancellationToken cancellationToken = default);
}

/// <summary>The only JavaScript boundary for the persisted browser database payload.</summary>
public sealed class JsBrowserDatabaseStorage : IBrowserDatabaseStorage
{
    private readonly IJSRuntime _js;

    public JsBrowserDatabaseStorage(IJSRuntime js) => _js = js;

    public ValueTask<StoredDatabase?> LoadAsync(CancellationToken cancellationToken = default) =>
        _js.InvokeAsync<StoredDatabase?>("workplanDb.load", cancellationToken);

    public ValueTask SaveAsync(StoredDatabase database, CancellationToken cancellationToken = default) =>
        _js.InvokeVoidAsync("workplanDb.save", cancellationToken, database.Data, database.Version);

    public ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
        _js.InvokeVoidAsync("workplanDb.clear", cancellationToken);

    public ValueTask ExportAsync(StoredDatabase database, CancellationToken cancellationToken = default) =>
        _js.InvokeVoidAsync("workplanDb.export", cancellationToken, database.Data, database.Version);
}
