using System.Globalization;
using System.Text;

namespace WorkPlanStudio.Export.Pdf;

/// <summary>
/// One page and its content stream.
/// <para>
/// Coordinates are given with the origin at the <em>top</em> left and y
/// increasing downwards, which is how a table lays itself out and how every
/// caller here thinks. PDF user space has its origin at the bottom left, so the
/// conversion happens in one place - here - rather than in every caller.
/// </para>
/// </summary>
public sealed class PdfPage
{
    private readonly MemoryStream _content = new();

    internal PdfPage(PdfPageSize size, PdfMargins margins)
    {
        Size = size;
        Margins = margins;
    }

    /// <summary>The sheet size in points.</summary>
    public PdfPageSize Size { get; }

    /// <summary>The margins in points.</summary>
    public PdfMargins Margins { get; }

    /// <summary>Left edge of the content area.</summary>
    public double ContentLeft => Margins.Left;

    /// <summary>Right edge of the content area.</summary>
    public double ContentRight => Size.Width - Margins.Right;

    /// <summary>Top edge of the content area, in top-down coordinates.</summary>
    public double ContentTop => Margins.Top;

    /// <summary>Bottom edge of the content area, in top-down coordinates.</summary>
    public double ContentBottom => Size.Height - Margins.Bottom;

    /// <summary>Width of the content area.</summary>
    public double ContentWidth => ContentRight - ContentLeft;

    /// <summary>Height of the content area.</summary>
    public double ContentHeight => ContentBottom - ContentTop;

    /// <summary>
    /// Draws <paramref name="text"/> with its baseline at
    /// (<paramref name="x"/>, <paramref name="baselineY"/>). Empty text draws
    /// nothing rather than an empty text object.
    /// </summary>
    public void Text(string? text, double x, double baselineY, PdfTextStyle style)
    {
        if (string.IsNullOrEmpty(text))
            return;

        Ascii("BT /");
        Ascii(style.Font == PdfFont.Bold ? "F2 " : "F1 ");
        Number(style.Size);
        Ascii(" Tf ");
        Ascii(style.Colour.Operands());
        Ascii(" rg ");
        Number(x);
        Ascii(" ");
        Number(Flip(baselineY));
        Ascii(" Td (");
        Raw(WinAnsiEncoding.EncodeLiteral(text));
        Ascii(") Tj ET\n");
    }

    /// <summary>Draws text whose right edge sits at <paramref name="right"/>.</summary>
    public void TextRight(string? text, double right, double baselineY, PdfTextStyle style) =>
        Text(text, right - style.Measure(text ?? ""), baselineY, style);

    /// <summary>Draws text centred on <paramref name="centre"/>.</summary>
    public void TextCentred(string? text, double centre, double baselineY, PdfTextStyle style) =>
        Text(text, centre - style.Measure(text ?? "") / 2, baselineY, style);

    /// <summary>Draws text aligned inside the span from <paramref name="left"/> to <paramref name="right"/>.</summary>
    public void TextAligned(string? text, double left, double right, double baselineY, PdfTextStyle style, ExportAlignment alignment)
    {
        switch (alignment)
        {
            case ExportAlignment.Right:
                TextRight(text, right, baselineY, style);
                break;
            case ExportAlignment.Centre:
                TextCentred(text, (left + right) / 2, baselineY, style);
                break;
            default:
                Text(text, left, baselineY, style);
                break;
        }
    }

    /// <summary>Strokes a straight line.</summary>
    public void Line(double x1, double y1, double x2, double y2, double width, PdfColour colour)
    {
        Ascii(colour.Operands());
        Ascii(" RG ");
        Number(width);
        Ascii(" w ");
        Number(x1);
        Ascii(" ");
        Number(Flip(y1));
        Ascii(" m ");
        Number(x2);
        Ascii(" ");
        Number(Flip(y2));
        Ascii(" l S\n");
    }

    /// <summary>Strokes the outline of a rectangle whose top-left corner is (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Rect(double x, double y, double width, double height, double lineWidth, PdfColour colour)
    {
        Ascii(colour.Operands());
        Ascii(" RG ");
        Number(lineWidth);
        Ascii(" w ");
        Rectangle(x, y, width, height);
        Ascii(" S\n");
    }

    /// <summary>Fills a rectangle whose top-left corner is (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void FilledRect(double x, double y, double width, double height, PdfColour colour)
    {
        Ascii(colour.Operands());
        Ascii(" rg ");
        Rectangle(x, y, width, height);
        Ascii(" f\n");
    }

    /// <summary>Fills a rectangle and strokes its outline in a second colour - a bar that has to read as late.</summary>
    public void FilledRect(double x, double y, double width, double height, PdfColour fill, PdfColour stroke, double lineWidth)
    {
        FilledRect(x, y, width, height, fill);
        Rect(x, y, width, height, lineWidth, stroke);
    }

    internal byte[] ContentBytes() => _content.ToArray();

    private void Rectangle(double x, double y, double width, double height)
    {
        Number(x);
        Ascii(" ");
        Number(Flip(y + height));
        Ascii(" ");
        Number(Math.Max(0, width));
        Ascii(" ");
        Number(Math.Max(0, height));
        Ascii(" re");
    }

    private double Flip(double y) => Size.Height - y;

    private void Number(double value)
    {
        // Three decimals is a twentieth of a typographic point - far below what
        // any output device resolves, and it keeps the stream compact.
        var text = value.ToString("0.###", CultureInfo.InvariantCulture);
        Ascii(text == "-0" ? "0" : text);
    }

    private void Ascii(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        _content.Write(bytes, 0, bytes.Length);
    }

    private void Raw(byte[] bytes) => _content.Write(bytes, 0, bytes.Length);
}
