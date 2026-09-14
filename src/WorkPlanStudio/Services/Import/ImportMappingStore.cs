using System.Text.Json;
using Microsoft.JSInterop;

namespace WorkPlanStudio.Services.Import;

/// <summary>
/// Remembers the column mapping per sheet, so the monthly export only has to be
/// mapped once.
/// </summary>
/// <remarks>
/// Per browser, like every other preference here, and keyed by entity kind
/// rather than by file name: the same planner re-imports the same shape of file,
/// and its name usually carries a date.
/// </remarks>
public interface IImportMappingStore
{
    ValueTask<IReadOnlyDictionary<string, string>> LoadAsync(
        ImportEntityKind kind, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        ImportEntityKind kind, IReadOnlyDictionary<string, string> mapping, CancellationToken cancellationToken = default);
}

/// <summary>The browser-backed store, over the settings bridge the app already has.</summary>
public sealed class JsImportMappingStore : IImportMappingStore
{
    private readonly IJSRuntime _js;

    public JsImportMappingStore(IJSRuntime js) => _js = js;

    public async ValueTask<IReadOnlyDictionary<string, string>> LoadAsync(
        ImportEntityKind kind, CancellationToken cancellationToken = default)
    {
        string? stored;
        try
        {
            stored = await _js.InvokeAsync<string?>("workplanSettings.get", cancellationToken, KeyFor(kind));
        }
        catch (JSException)
        {
            // A browser with storage disabled has no remembered mapping, which is
            // the same situation as a first visit and not an error worth a banner.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (string.IsNullOrWhiteSpace(stored))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stored)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public async ValueTask SaveAsync(
        ImportEntityKind kind, IReadOnlyDictionary<string, string> mapping, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        try
        {
            await _js.InvokeVoidAsync(
                "workplanSettings.set", cancellationToken, KeyFor(kind), JsonSerializer.Serialize(mapping));
        }
        catch (JSException)
        {
            // Losing a convenience is not worth failing the import that produced it.
        }
    }

    private static string KeyFor(ImportEntityKind kind) => "import.map." + kind;
}
