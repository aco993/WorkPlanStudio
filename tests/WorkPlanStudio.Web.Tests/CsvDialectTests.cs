using System.Text;
using WorkPlanStudio.Services.Import;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Detection, which is the part of a CSV importer that is guessing. Each test
/// here is a file this app will be handed: a German Excel export, a Mac export,
/// a "Unicode text" export from a database tool, and the ambiguous cases where
/// the obvious heuristic gets it wrong.
/// </summary>
public sealed class CsvDialectTests
{
    private static CsvDialect Detect(byte[] bytes) => CsvDialectDetector.Detect(bytes, complete: true);

    private static byte[] Utf8(string text, bool bom = false) =>
        bom ? [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)] : Encoding.UTF8.GetBytes(text);

    private const string GermanSample =
        "Kennung;Bezeichnung;Stundensatz\nGRD-100;Flächenschleifer;74,50\nDRL-200;Bohrmaschine;41,00\n";

    // ----- encoding -------------------------------------------------------

    [Fact]
    public void Utf8_without_a_mark_is_recognised_from_its_own_structure()
    {
        var dialect = Detect(Utf8(GermanSample));

        Assert.Equal("UTF-8", dialect.EncodingName);
        Assert.False(dialect.HasByteOrderMark);
    }

    [Fact]
    public void A_utf8_mark_is_reported_and_skipped()
    {
        var dialect = Detect(Utf8(GermanSample, bom: true));

        Assert.True(dialect.HasByteOrderMark);
        Assert.Equal(';', dialect.Separator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16_is_recognised_from_its_mark_in_either_byte_order(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true);
        var bytes = (byte[])[.. encoding.GetPreamble(), .. encoding.GetBytes(GermanSample)];

        var dialect = Detect(bytes);

        Assert.True(dialect.HasByteOrderMark);
        Assert.Equal(';', dialect.Separator);
    }

    /// <summary>
    /// Without a mark, UTF-16 is not valid UTF-8 and would otherwise be read as
    /// Windows-1252 — a NUL between every letter, and a header nothing matches.
    /// </summary>
    [Fact]
    public void Utf16_without_a_mark_is_recognised_from_its_nul_padding()
    {
        var bytes = new UnicodeEncoding(false, false).GetBytes(GermanSample);

        var (encoding, bom) = CsvDialectDetector.DetectEncoding(bytes);

        Assert.Equal(0, bom);
        Assert.Equal("Kennung", encoding.GetString(bytes)[..7]);
    }

    [Fact]
    public void A_windows_1252_export_is_not_mistaken_for_utf8()
    {
        var bytes = Encoding.Latin1.GetBytes(GermanSample);

        var (encoding, _) = CsvDialectDetector.DetectEncoding(bytes);

        Assert.Equal("Windows-1252", encoding.EncodingName);
        Assert.Contains("Flächenschleifer", encoding.GetString(bytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// The 0x80-0x9F range is the only place Windows-1252 differs from Latin-1,
    /// and it is where the characters Word substitutes live: a curly quote in a
    /// part name decodes to a control character under Latin-1 and to a quote here.
    /// </summary>
    [Fact]
    public void The_windows_1252_control_range_decodes_to_its_real_characters()
    {
        byte[] bytes = [0x93, (byte)'A', 0x94, 0x96, 0x80, 0x92];

        var (encoding, _) = CsvDialectDetector.DetectEncoding([.. bytes, .. Encoding.Latin1.GetBytes("ä")]);

        Assert.Equal("“A”–€’", encoding.GetString(bytes));
    }

    /// <summary>
    /// Pure ASCII satisfies both candidates, so the guess cannot be wrong — which
    /// is the reason the fallback is allowed to be a guess at all.
    /// </summary>
    [Fact]
    public void Pure_ascii_decodes_identically_under_either_candidate()
    {
        var bytes = "Code,Name\nCC-1,Machining\n"u8.ToArray();

        var (detected, _) = CsvDialectDetector.DetectEncoding(bytes);

        Assert.Equal(Encoding.UTF8.GetString(bytes), detected.GetString(bytes));
        Assert.Equal(Encoding.Latin1.GetString(bytes), detected.GetString(bytes));
    }

    // ----- separator ------------------------------------------------------

    [Theory]
    [InlineData(';')]
    [InlineData(',')]
    [InlineData('\t')]
    public void Each_supported_separator_is_detected(char separator)
    {
        var text = $"Code{separator}Name{separator}Rate\nCC-1{separator}Machining{separator}74.50\nCC-2{separator}Sawing{separator}41.00\n";

        Assert.Equal(separator, Detect(Utf8(text)).Separator);
    }

    /// <summary>
    /// The case that breaks "count the commas": four semicolons per row, and one
    /// description field with three commas in it.
    /// </summary>
    [Fact]
    public void A_comma_inside_a_field_does_not_outvote_the_real_separator()
    {
        var text =
            "Code;Name;Note\n" +
            "CC-1;Machining;\"Turning, milling, and deburring\"\n" +
            "CC-2;Sawing;\"Cut-off, then chamfer\"\n";

        Assert.Equal(';', Detect(Utf8(text)).Separator);
    }

    [Fact]
    public void A_single_column_file_is_left_to_the_mapper_rather_than_guessed_at()
    {
        // Nothing to detect. Whatever is chosen, the file has one column and the
        // required-field check is what refuses it — not a wrong separator that
        // pretends the file was read.
        var dialect = Detect(Utf8("Code\nCC-1\nCC-2\n"));

        Assert.Contains(dialect.Separator, CsvDialectDetector.Separators);
    }

    [Fact]
    public void A_truncated_last_line_does_not_skew_the_guess()
    {
        var sample = "a;b;c\nd;e;f\ng;h";   // the sniff buffer cut the file mid-row

        Assert.Equal(';', CsvDialectDetector.DetectSeparator(sample, complete: false));
    }

    /// <summary>
    /// The name on screen comes from a table here, not from
    /// <c>Encoding.EncodingName</c>: a trimmed WebAssembly build ships no
    /// globalization resources, so that property renders as
    /// <c>Globalization_cp_65001</c> in the published app and nowhere else.
    /// </summary>
    [Theory]
    [InlineData(65001, "UTF-8")]
    [InlineData(1200, "UTF-16 LE")]
    [InlineData(1201, "UTF-16 BE")]
    [InlineData(1252, "Windows-1252")]
    public void The_encoding_is_named_from_a_table_that_ships_with_the_app(int codePage, string expected)
    {
        var encoding = codePage switch
        {
            65001 => (Encoding)new UTF8Encoding(false),
            1200 => new UnicodeEncoding(false, false),
            1201 => new UnicodeEncoding(true, false),
            _ => CsvDialectDetector.DetectEncoding(Encoding.Latin1.GetBytes(GermanSample)).Encoding
        };

        Assert.Equal(expected, CsvDialectDetector.Label(encoding));
    }

    [Fact]
    public void The_tab_separator_is_labelled_rather_than_rendered()
    {
        Assert.Equal("TAB", new CsvDialect('\t', "UTF-8", false).SeparatorLabel);
        Assert.Equal(";", new CsvDialect(';', "UTF-8", false).SeparatorLabel);
    }
}
