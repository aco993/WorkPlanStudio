namespace WorkPlanStudio.Export.Pdf;

/// <summary>
/// Glyph advance widths for the two standard fonts the writer uses, and the
/// measuring built on them.
/// <para>
/// A PDF that uses a standard font ships no font programme, so a reader that
/// wants to know how wide a string will be has to know the metrics itself. The
/// numbers below are Adobe's published AFM advance widths for Helvetica and
/// Helvetica-Bold, in units of 1/1000 em, indexed by WinAnsi code rather than
/// by glyph name - which is the form the measuring actually needs. A code the
/// encoding leaves undefined has width 0 and can never be reached, because
/// <see cref="WinAnsiEncoding.Encode"/> substitutes before it gets here.
/// </para>
/// <para>
/// Without this table there is no truncation, no column alignment and no
/// centring: every string would have to be assumed as wide as its longest
/// possible rendering, and a table would either overflow or waste half the page.
/// </para>
/// </summary>
public static class StandardFonts
{
    /// <summary>Adobe's em square for the standard fonts: widths are thousandths of the point size.</summary>
    public const double UnitsPerEm = 1000.0;

    private static readonly short[] HelveticaWidths =
    [
        // 0x00-0x1F - undefined in WinAnsiEncoding
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 0x20 space ! " # $ % & ' ( ) * + , - . /
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        // 0x30 0-9 : ; < = > ?
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        // 0x40 @ A-O
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        // 0x50 P-Z [ \ ] ^ _
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        // 0x60 ` a-o
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        // 0x70 p-z { | } ~ (0x7F undefined)
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, 0,
        // 0x80 Euro . quotesinglbase florin quotedblbase ellipsis dagger daggerdbl
        //      circumflex perthousand Scaron guilsinglleft OE . Zcaron .
        556, 0, 222, 556, 333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0, 611, 0,
        // 0x90 . quoteleft quoteright quotedblleft quotedblright bullet endash emdash
        //      tilde trademark scaron guilsinglright oe . zcaron Ydieresis
        0, 222, 222, 333, 333, 350, 556, 1000, 333, 1000, 500, 333, 944, 0, 500, 667,
        // 0xA0 nbsp exclamdown cent sterling currency yen brokenbar section
        //      dieresis copyright ordfeminine guillemotleft logicalnot hyphen registered macron
        278, 333, 556, 556, 556, 556, 260, 556, 333, 737, 370, 556, 584, 333, 737, 333,
        // 0xB0 degree plusminus twosuperior threesuperior acute mu paragraph periodcentered
        //      cedilla onesuperior ordmasculine guillemotright onequarter onehalf threequarters questiondown
        400, 584, 333, 333, 333, 556, 537, 278, 333, 333, 365, 556, 834, 834, 834, 611,
        // 0xC0 Agrave-Odieresis (A-family 667, C 722, E 667, I 278, Eth/Ntilde 722, O 778)
        667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278,
        // 0xD0 Eth Ntilde Ograve-Odieresis multiply Oslash Ugrave-Udieresis Yacute Thorn germandbls
        722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611,
        // 0xE0 agrave-odieresis
        556, 556, 556, 556, 556, 556, 889, 500, 556, 556, 556, 556, 278, 278, 278, 278,
        // 0xF0 eth ntilde ograve-odieresis divide oslash ugrave-udieresis yacute thorn ydieresis
        556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556, 556, 500, 556, 500
    ];

    private static readonly short[] HelveticaBoldWidths =
    [
        // 0x00-0x1F - undefined in WinAnsiEncoding
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 0x20 space ! " # $ % & ' ( ) * + , - . /
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        // 0x30 0-9 : ; < = > ?
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        // 0x40 @ A-O
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        // 0x50 P-Z [ \ ] ^ _
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        // 0x60 ` a-o
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        // 0x70 p-z { | } ~ (0x7F undefined)
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584, 0,
        // 0x80 Euro . quotesinglbase florin quotedblbase ellipsis dagger daggerdbl
        //      circumflex perthousand Scaron guilsinglleft OE . Zcaron .
        556, 0, 278, 556, 500, 1000, 556, 556, 333, 1000, 667, 333, 1000, 0, 611, 0,
        // 0x90 . quoteleft quoteright quotedblleft quotedblright bullet endash emdash
        //      tilde trademark scaron guilsinglright oe . zcaron Ydieresis
        0, 278, 278, 500, 500, 350, 556, 1000, 333, 1000, 556, 333, 944, 0, 500, 667,
        // 0xA0 nbsp exclamdown cent sterling currency yen brokenbar section
        //      dieresis copyright ordfeminine guillemotleft logicalnot hyphen registered macron
        278, 333, 556, 556, 556, 556, 280, 556, 333, 737, 370, 556, 584, 333, 737, 333,
        // 0xB0 degree plusminus twosuperior threesuperior acute mu paragraph periodcentered
        //      cedilla onesuperior ordmasculine guillemotright onequarter onehalf threequarters questiondown
        400, 584, 333, 333, 333, 611, 556, 278, 333, 333, 365, 556, 834, 834, 834, 611,
        // 0xC0 Agrave-Odieresis
        722, 722, 722, 722, 722, 722, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278,
        // 0xD0 Eth Ntilde Ograve-Odieresis multiply Oslash Ugrave-Udieresis Yacute Thorn germandbls
        722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611,
        // 0xE0 agrave-odieresis
        556, 556, 556, 556, 556, 556, 889, 556, 556, 556, 556, 556, 278, 278, 278, 278,
        // 0xF0 eth ntilde ograve-odieresis divide oslash ugrave-udieresis yacute thorn ydieresis
        611, 611, 611, 611, 611, 611, 611, 584, 611, 611, 611, 611, 611, 556, 611, 556
    ];

    /// <summary>The PDF base font name, as it appears in the font dictionary.</summary>
    public static string BaseFontName(PdfFont font) => font == PdfFont.Bold ? "Helvetica-Bold" : "Helvetica";

    /// <summary>The advance width of one WinAnsi code, in thousandths of an em.</summary>
    public static short WidthOf(PdfFont font, byte code) =>
        (font == PdfFont.Bold ? HelveticaBoldWidths : HelveticaWidths)[code];

    /// <summary>The width <paramref name="text"/> occupies at <paramref name="size"/> points.</summary>
    public static double Measure(string? text, PdfFont font, double size)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var widths = font == PdfFont.Bold ? HelveticaBoldWidths : HelveticaWidths;
        long total = 0;
        foreach (var character in text)
            total += widths[WinAnsiEncoding.Encode(character)];

        return total * size / UnitsPerEm;
    }

    /// <summary>
    /// Shortens <paramref name="text"/> until it fits <paramref name="maxWidth"/>,
    /// ending with an ellipsis so the reader can see something was cut. Returns
    /// an empty string when not even the ellipsis fits, which is the honest
    /// answer for a column that narrow.
    /// </summary>
    public static string Truncate(string? text, PdfFont font, double size, double maxWidth)
    {
        if (string.IsNullOrEmpty(text) || Measure(text, font, size) <= maxWidth)
            return text ?? "";

        const string Ellipsis = "…";
        double ellipsisWidth = Measure(Ellipsis, font, size);
        if (ellipsisWidth > maxWidth)
            return "";

        double budget = maxWidth - ellipsisWidth;
        double used = 0;
        int taken = 0;
        foreach (var character in text)
        {
            double width = WidthOf(font, WinAnsiEncoding.Encode(character)) * size / UnitsPerEm;
            if (used + width > budget)
                break;
            used += width;
            taken++;
        }

        return text[..taken].TrimEnd() + Ellipsis;
    }

    /// <summary>
    /// Breaks <paramref name="text"/> into lines no wider than
    /// <paramref name="maxWidth"/>, at spaces where possible and inside a word
    /// when a single word is wider than the line.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string? text, PdfFont font, double size, double maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text) || maxWidth <= 0)
            return [];

        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (Measure(candidate, font, size) <= maxWidth)
            {
                line = candidate;
                continue;
            }

            if (line.Length > 0)
            {
                lines.Add(line);
                line = "";
            }

            // A word that cannot fit on a line of its own is split by character;
            // hyphenation would need a dictionary this library has no business
            // carrying.
            var remainder = word;
            while (Measure(remainder, font, size) > maxWidth)
            {
                int fits = 1;
                while (fits < remainder.Length && Measure(remainder[..(fits + 1)], font, size) <= maxWidth)
                    fits++;
                lines.Add(remainder[..fits]);
                remainder = remainder[fits..];
            }

            line = remainder;
        }

        if (line.Length > 0)
            lines.Add(line);

        return lines;
    }
}
