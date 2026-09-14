using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WorkPlanStudio.Export.Pdf;

/// <summary>Document-wide settings: page geometry and what goes in the information dictionary.</summary>
public sealed record PdfDocumentOptions
{
    /// <summary>Sheet size before orientation is applied.</summary>
    public PdfPageSize PageSize { get; init; } = PdfPageSize.A4;

    /// <summary>Orientation. A schedule is wide, so landscape is the usual answer.</summary>
    public PdfPageOrientation Orientation { get; init; } = PdfPageOrientation.Landscape;

    /// <summary>Margins in points. 36 pt is half an inch.</summary>
    public PdfMargins Margins { get; init; } = PdfMargins.All(36);

    /// <summary>Document title, shown in the reader's title bar and used by search.</summary>
    public string Title { get; init; } = "";

    /// <summary>Author, written to the information dictionary.</summary>
    public string? Author { get; init; }

    /// <summary>Subject line, written to the information dictionary.</summary>
    public string? Subject { get; init; }

    /// <summary>The software that produced the file.</summary>
    public string Producer { get; init; } = "WorkPlan Studio";

    /// <summary>
    /// BCP 47 language tag for the document, so a screen reader pronounces the
    /// text with the right rules rather than guessing from the characters.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>Creation timestamp, written to the information dictionary.</summary>
    public DateTimeOffset CreatedAt { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Assembles pages into a PDF 1.7 file: objects, a cross-reference table and a
/// trailer, written by hand.
/// <para>
/// The cross-reference table is the part that has to be exactly right. It is a
/// byte offset per object, and a reader trusts it absolutely - an offset that is
/// one byte out produces a file that opens in one viewer and fails in the next,
/// with no diagnostic. The offsets are therefore recorded as the bytes are
/// written, never computed afterwards, and a test parses them back and checks
/// each one really does land on the object header it claims.
/// </para>
/// <para>
/// Content streams are left uncompressed. Flate would shrink the file, but the
/// documents here are a few hundred kilobytes, and an uncompressed stream is one
/// a reviewer (or a test) can read.
/// </para>
/// </summary>
public sealed class PdfDocumentWriter
{
    private readonly List<PdfPage> _pages = [];
    private readonly PdfPageSize _size;

    /// <summary>Starts an empty document.</summary>
    public PdfDocumentWriter(PdfDocumentOptions? options = null)
    {
        Options = options ?? new PdfDocumentOptions();
        _size = Options.PageSize.In(Options.Orientation);
    }

    /// <summary>The MIME type of the produced file.</summary>
    public const string ContentType = "application/pdf";

    /// <summary>The settings this document was started with.</summary>
    public PdfDocumentOptions Options { get; }

    /// <summary>The pages added so far, in order.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>Appends a page and returns it for drawing.</summary>
    public PdfPage AddPage()
    {
        var page = new PdfPage(_size, Options.Margins);
        _pages.Add(page);
        return page;
    }

    /// <summary>
    /// Serialises the document. A document with no page gets one blank page,
    /// because a PDF with an empty page tree is invalid and a reader will refuse
    /// the file outright.
    /// </summary>
    public byte[] ToArray()
    {
        if (_pages.Count == 0)
            AddPage();

        // Object 1 catalogue, 2 page tree, 3 and 4 the two fonts, then two
        // objects per page, then the information dictionary last.
        const int FirstPageObject = 5;
        int infoObject = FirstPageObject + _pages.Count * 2;

        // /Marked false rather than true: nothing here builds a tagged structure
        // tree, and claiming one a reader cannot follow is worse than admitting
        // there is none.
        var catalogue = $"<< /Type /Catalog /Pages 2 0 R /Lang ({Escape(Options.Language)}) /MarkInfo << /Marked false >> >>";
        var objects = new List<byte[]>(infoObject) { Latin1(catalogue) };

        var kids = string.Join(" ", Enumerable.Range(0, _pages.Count).Select(i => $"{FirstPageObject + i * 2} 0 R"));
        objects.Add(Latin1($"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>"));

        objects.Add(Latin1($"<< /Type /Font /Subtype /Type1 /BaseFont /{StandardFonts.BaseFontName(PdfFont.Regular)} /Encoding /WinAnsiEncoding >>"));
        objects.Add(Latin1($"<< /Type /Font /Subtype /Type1 /BaseFont /{StandardFonts.BaseFontName(PdfFont.Bold)} /Encoding /WinAnsiEncoding >>"));

        for (int i = 0; i < _pages.Count; i++)
        {
            int pageObject = FirstPageObject + i * 2;
            var page = _pages[i];
            objects.Add(Latin1(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Size.Width:0.###} {page.Size.Height:0.###}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageObject + 1} 0 R >>")));
            objects.Add(Stream(page.ContentBytes()));
        }

        objects.Add(Latin1(InformationDictionary()));

        return Assemble(objects);
    }

    private byte[] Assemble(IReadOnlyList<byte[]> objects)
    {
        using var output = new MemoryStream();
        Append(output, "%PDF-1.7\n");

        // A comment of bytes above 127 on line 2 is the convention that tells
        // file-transfer tools the file is binary and must not be line-ending
        // converted.
        output.WriteByte((byte)'%');
        output.Write([0xE2, 0xE3, 0xCF, 0xD3]);
        output.WriteByte((byte)'\n');

        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = output.Position;
            Append(output, string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n"));
            output.Write(objects[i], 0, objects[i].Length);
            Append(output, "\nendobj\n");
        }

        long startXref = output.Position;
        Append(output, string.Create(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n"));
        Append(output, "0000000000 65535 f \n");
        foreach (var offset in offsets)
            Append(output, string.Create(CultureInfo.InvariantCulture, $"{offset:0000000000} 00000 n \n"));

        var id = DocumentId();
        Append(output, string.Create(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info {objects.Count} 0 R /ID [<{id}> <{id}>] >>\n"));
        Append(output, string.Create(CultureInfo.InvariantCulture, $"startxref\n{startXref}\n%%EOF\n"));

        return output.ToArray();
    }

    private string InformationDictionary()
    {
        var builder = new StringBuilder("<< ");
        builder.Append("/Title (").Append(Escape(Options.Title)).Append(") ");
        if (!string.IsNullOrEmpty(Options.Author))
            builder.Append("/Author (").Append(Escape(Options.Author)).Append(") ");
        if (!string.IsNullOrEmpty(Options.Subject))
            builder.Append("/Subject (").Append(Escape(Options.Subject)).Append(") ");

        return builder
            .Append("/Producer (").Append(Escape(Options.Producer)).Append(") ")
            .Append("/Creator (").Append(Escape(Options.Producer)).Append(") ")
            .Append("/CreationDate (").Append(PdfDate(Options.CreatedAt)).Append(") ")
            .Append("/ModDate (").Append(PdfDate(Options.CreatedAt)).Append(") >>")
            .ToString();
    }

    /// <summary>The PDF date syntax: <c>D:YYYYMMDDHHmmSSOHH'mm</c>.</summary>
    internal static string PdfDate(DateTimeOffset moment)
    {
        var offset = moment.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        return string.Create(CultureInfo.InvariantCulture,
            $"D:{moment:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}");
    }

    /// <summary>
    /// A stable file identifier derived from the title and the creation date.
    /// PDF wants two identifiers; an unmodified document has the same value for
    /// both. It is a fingerprint for change detection, never a security
    /// property - the hash is used only because it produces well-distributed
    /// bytes from a short string.
    /// </summary>
    private string DocumentId()
    {
        var seed = Encoding.UTF8.GetBytes(Options.Title + "|" + PdfDate(Options.CreatedAt) + "|" + _pages.Count);
        return Convert.ToHexString(SHA256.HashData(seed).AsSpan(0, 16));
    }

    private static byte[] Stream(byte[] content)
    {
        var header = Latin1(string.Create(CultureInfo.InvariantCulture, $"<< /Length {content.Length} >>\nstream\n"));
        var footer = Latin1("\nendstream");
        var result = new byte[header.Length + content.Length + footer.Length];
        header.CopyTo(result, 0);
        content.CopyTo(result, header.Length);
        footer.CopyTo(result, header.Length + content.Length);
        return result;
    }

    private static string Escape(string value) =>
        Encoding.Latin1.GetString(WinAnsiEncoding.EncodeLiteral(value));

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static void Append(Stream stream, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }
}
