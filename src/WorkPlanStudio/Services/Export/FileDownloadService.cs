using Microsoft.JSInterop;

namespace WorkPlanStudio.Services;

/// <summary>
/// Hands a byte array to the browser as a download.
/// <para>
/// The bytes travel as a <see cref="DotNetStreamReference"/> rather than a
/// base64 string. Base64 through interop inflates the payload by a third and
/// forces the whole thing through the JSON marshaller twice - once encoding in
/// .NET, once decoding in JavaScript - which for a multi-megabyte workbook is
/// a visible pause on the UI thread. The stream reference transfers the raw
/// buffer instead.
/// </para>
/// <para>
/// The object URL is revoked as soon as the click has been dispatched. A blob
/// URL pins its blob in memory for the life of the document, so a planner who
/// exports a dozen times without reloading would otherwise be holding a dozen
/// copies of the file.
/// </para>
/// </summary>
public sealed class FileDownloadService
{
    /// <summary>The JavaScript function the service calls.</summary>
    public const string JsFunction = "workplanDownload.save";

    private readonly IJSRuntime _js;

    /// <summary>Creates the service over the app's JS runtime.</summary>
    public FileDownloadService(IJSRuntime js) => _js = js;

    /// <summary>Saves <paramref name="content"/> under <paramref name="fileName"/>.</summary>
    /// <param name="content">The file's bytes.</param>
    /// <param name="fileName">The name the browser suggests; already sanitised by the caller.</param>
    /// <param name="contentType">The media type the blob is created with.</param>
    /// <param name="cancellationToken">Abandons the call if the page goes away mid-download.</param>
    public async Task SaveAsync(byte[] content, string fileName, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        using var stream = new MemoryStream(content, writable: false);
        using var reference = new DotNetStreamReference(stream, leaveOpen: true);
        await _js.InvokeVoidAsync(JsFunction, cancellationToken, reference, fileName, contentType);
    }
}
