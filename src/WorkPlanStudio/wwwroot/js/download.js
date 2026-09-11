// Turns bytes produced in .NET into a browser download.
//
// The bytes arrive as a Blazor stream reference rather than a base64 string:
// arrayBuffer() gives the raw buffer, so a multi-megabyte workbook does not
// have to be encoded, shipped through the JSON channel and decoded again.
//
// The object URL is revoked in a microtask after the click rather than
// immediately, because Safari dispatches the download asynchronously and
// revoking in the same turn cancels it. It is revoked either way: a blob URL
// keeps its blob alive for the life of the document, so exporting repeatedly
// without a reload would otherwise accumulate copies of every file.

window.workplanDownload = {
    save: async function (streamReference, fileName, contentType) {
        const buffer = await streamReference.arrayBuffer();
        const blob = new Blob([buffer], { type: contentType });
        const url = URL.createObjectURL(blob);

        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        link.rel = 'noopener';
        link.style.display = 'none';

        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);

        setTimeout(() => URL.revokeObjectURL(url), 0);
    }
};
