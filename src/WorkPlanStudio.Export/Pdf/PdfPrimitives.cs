using System.Globalization;

namespace WorkPlanStudio.Export.Pdf;

/// <summary>
/// The two typefaces the writer uses. Both are among the fourteen standard
/// Type 1 fonts every PDF reader is required to provide, which is what lets the
/// file carry no font programme at all - and therefore no font parser, no
/// subsetting and no native code.
/// </summary>
public enum PdfFont
{
    /// <summary>Helvetica.</summary>
    Regular,

    /// <summary>Helvetica-Bold.</summary>
    Bold
}

/// <summary>Page orientation. A schedule is wide, so landscape is the sensible default.</summary>
public enum PdfPageOrientation
{
    /// <summary>Taller than wide.</summary>
    Portrait,

    /// <summary>Wider than tall.</summary>
    Landscape
}

/// <summary>An RGB colour with components in 0..1, the range PDF's colour operators take.</summary>
/// <param name="Red">Red component, 0..1.</param>
/// <param name="Green">Green component, 0..1.</param>
/// <param name="Blue">Blue component, 0..1.</param>
public readonly record struct PdfColour(double Red, double Green, double Blue)
{
    /// <summary>A colour from the usual 8-bit components.</summary>
    public static PdfColour FromRgb(int red, int green, int blue) => new(red / 255.0, green / 255.0, blue / 255.0);

    /// <summary>Black.</summary>
    public static PdfColour Black { get; } = new(0, 0, 0);

    /// <summary>White.</summary>
    public static PdfColour White { get; } = new(1, 1, 1);

    /// <summary>The body text grey - not quite black, which reads better on paper.</summary>
    public static PdfColour Text { get; } = FromRgb(0x1f, 0x24, 0x37);

    /// <summary>Secondary text.</summary>
    public static PdfColour Muted { get; } = FromRgb(0x6b, 0x72, 0x80);

    /// <summary>Hairlines and table rules.</summary>
    public static PdfColour Rule { get; } = FromRgb(0xd1, 0xd5, 0xdb);

    /// <summary>The zebra stripe and the table header fill.</summary>
    public static PdfColour Tint { get; } = FromRgb(0xf3, 0xf4, 0xf6);

    /// <summary>The accent used for the title rule.</summary>
    public static PdfColour Accent { get; } = FromRgb(0x4f, 0x46, 0xe5);

    /// <summary>The warning colour that marks a late bar.</summary>
    public static PdfColour Late { get; } = FromRgb(0xb9, 0x1c, 0x1c);

    /// <summary>
    /// The eight job colours, in the order the web UI cycles through them, so a
    /// printed chart and the screen it came from agree on which bar is which job.
    /// </summary>
    public static IReadOnlyList<PdfColour> Palette { get; } =
    [
        FromRgb(0x4f, 0x46, 0xe5), FromRgb(0x0e, 0x74, 0x90), FromRgb(0x15, 0x80, 0x3d), FromRgb(0xb4, 0x53, 0x09),
        FromRgb(0xbe, 0x18, 0x5d), FromRgb(0x7c, 0x3a, 0xed), FromRgb(0xb9, 0x1c, 0x1c), FromRgb(0x0f, 0x76, 0x6e)
    ];

    /// <summary>Palette entry <paramref name="index"/>, wrapping so any index is safe.</summary>
    public static PdfColour FromPalette(int index) =>
        Palette[((index % Palette.Count) + Palette.Count) % Palette.Count];

    /// <summary>The three operands of a PDF colour operator, invariantly formatted.</summary>
    internal string Operands() => string.Create(CultureInfo.InvariantCulture, $"{Clamp(Red):0.###} {Clamp(Green):0.###} {Clamp(Blue):0.###}");

    private static double Clamp(double component) => Math.Clamp(component, 0, 1);
}

/// <summary>Page size in PostScript points (1/72 inch), the only unit PDF user space has.</summary>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
public readonly record struct PdfPageSize(double Width, double Height)
{
    /// <summary>A4 portrait: 210 × 297 mm.</summary>
    public static PdfPageSize A4 { get; } = new(595.276, 841.89);

    /// <summary>US Letter portrait, for a reader who prints on it.</summary>
    public static PdfPageSize Letter { get; } = new(612, 792);

    /// <summary>The same sheet in the requested orientation.</summary>
    public PdfPageSize In(PdfPageOrientation orientation) => orientation switch
    {
        PdfPageOrientation.Landscape when Width < Height => new PdfPageSize(Height, Width),
        PdfPageOrientation.Portrait when Width > Height => new PdfPageSize(Height, Width),
        _ => this
    };
}

/// <summary>Page margins in points.</summary>
/// <param name="Left">Left margin.</param>
/// <param name="Top">Top margin.</param>
/// <param name="Right">Right margin.</param>
/// <param name="Bottom">Bottom margin.</param>
public readonly record struct PdfMargins(double Left, double Top, double Right, double Bottom)
{
    /// <summary>The same margin on all four sides.</summary>
    public static PdfMargins All(double points) => new(points, points, points, points);
}

/// <summary>How a run of text is drawn.</summary>
/// <param name="Font">Typeface.</param>
/// <param name="Size">Size in points.</param>
/// <param name="Colour">Fill colour.</param>
public readonly record struct PdfTextStyle(PdfFont Font, double Size, PdfColour Colour)
{
    /// <summary>Body text.</summary>
    public static PdfTextStyle Body { get; } = new(PdfFont.Regular, 9, PdfColour.Text);

    /// <summary>A heading.</summary>
    public static PdfTextStyle Heading { get; } = new(PdfFont.Bold, 13, PdfColour.Text);

    /// <summary>Small secondary text - captions, axis labels, the footer.</summary>
    public static PdfTextStyle Caption { get; } = new(PdfFont.Regular, 7.5, PdfColour.Muted);

    /// <summary>The width this style would need for <paramref name="text"/>, in points.</summary>
    public double Measure(string text) => StandardFonts.Measure(text, Font, Size);
}
