using System.Globalization;
using System.Text;
using WorkPlanStudio.Export.Pdf;

namespace WorkPlanStudio.Export.Tests;

/// <summary>
/// A PDF is a graph of objects addressed by byte offset. Nothing about it is
/// self-describing, so these tests read the file back the way a reader would -
/// trailer, cross-reference table, object headers - rather than trusting that
/// the writer meant well.
/// </summary>
public class PdfWriterTests
{
    private static byte[] Report(ExportReport? report = null) =>
        PdfReportWriter.Write(report ?? Sample.Report());

    [Fact]
    public void The_file_starts_and_ends_the_way_the_specification_says()
    {
        var bytes = Report();

        Assert.Equal("%PDF-1.7", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.EndsWith("%%EOF\n", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);

        // The second line must be a comment of high bytes, or a transfer that
        // "helpfully" converts line endings will corrupt the streams.
        Assert.True(bytes[9] == '%' && bytes[10] > 127, "the binary marker comment is missing");
    }

    /// <summary>
    /// The test that proves the writer is correct rather than merely plausible:
    /// every offset in the cross-reference table must land exactly on the header
    /// of the object it claims. One byte out and some readers cope, some do not.
    /// </summary>
    [Fact]
    public void Every_cross_reference_offset_lands_on_the_object_it_claims()
    {
        var probe = new PdfProbe(Report());

        Assert.NotEmpty(probe.Offsets);
        for (int objectNumber = 1; objectNumber <= probe.Offsets.Count; objectNumber++)
            Assert.Equal($"{objectNumber} 0 obj", probe.ObjectHeaderAt(objectNumber));
    }

    [Fact]
    public void The_trailer_agrees_with_the_table_about_how_many_objects_there_are()
    {
        var probe = new PdfProbe(Report());
        var size = System.Text.RegularExpressions.Regex.Match(probe.Text, @"/Size (?<size>\d+)").Groups["size"].Value;

        // /Size counts the objects plus the free entry at index 0.
        Assert.Equal(probe.Offsets.Count + 1, int.Parse(size, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Every_content_stream_declares_its_real_length()
    {
        // A /Length that disagrees with the bytes is the classic hand-written-PDF
        // fault: the reader stops mid-stream and the page comes out blank.
        var probe = new PdfProbe(Report(Sample.Report() with { Tables = [Sample.Large(400)] }));
        var ranges = probe.StreamRanges();

        Assert.NotEmpty(ranges);
        foreach (var (start, length) in ranges)
            Assert.Equal("\nendstream", probe.Text.Substring(start + length, 10));
    }

    [Fact]
    public void The_document_declares_its_metadata_honestly()
    {
        var probe = new PdfProbe(Report());

        Assert.Contains("/Title (Production schedule)", probe.Text, StringComparison.Ordinal);
        Assert.Contains("/Producer (WorkPlan Studio)", probe.Text, StringComparison.Ordinal);
        Assert.Contains("/CreationDate (D:20260911090000+00'00)", probe.Text, StringComparison.Ordinal);
        Assert.Contains("/Lang (en)", probe.Text, StringComparison.Ordinal);

        // Nothing here builds a tagged structure tree, so the document says so
        // rather than claiming an accessibility it does not deliver.
        Assert.Contains("/MarkInfo << /Marked false >>", probe.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_language_follows_the_culture_the_report_was_formatted_for()
    {
        var probe = new PdfProbe(Report(Sample.Report(Sample.German)));
        Assert.Contains("/Lang (de)", probe.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_standard_fonts_are_declared_without_being_embedded()
    {
        var probe = new PdfProbe(Report());

        Assert.Contains("/BaseFont /Helvetica /Encoding /WinAnsiEncoding", probe.Text, StringComparison.Ordinal);
        Assert.Contains("/BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding", probe.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/FontFile", probe.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfPageOrientation.Landscape, 841.89, 595.276)]
    [InlineData(PdfPageOrientation.Portrait, 595.276, 841.89)]
    public void A4_is_a4_in_either_orientation(PdfPageOrientation orientation, double width, double height)
    {
        var probe = new PdfProbe(PdfReportWriter.Write(Sample.Report(), new PdfReportOptions { Orientation = orientation }));

        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"/MediaBox [0 0 {width:0.###} {height:0.###}]"),
            probe.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_that_does_not_fit_is_carried_onto_further_pages()
    {
        var oneScreen = new PdfProbe(PdfReportWriter.Write(Sample.Report()));
        var many = new PdfProbe(PdfReportWriter.Write(
            Sample.Report() with { Tables = [Sample.Large(400)], Gantt = null }));

        Assert.Equal(1, oneScreen.PageCount);
        Assert.True(many.PageCount > 1, "400 rows should not fit on one page");
        Assert.Equal(many.PageCount, many.ContentStreams().Count);
    }

    [Fact]
    public void The_header_row_is_repeated_on_every_page_of_a_long_table()
    {
        var probe = new PdfProbe(PdfReportWriter.Write(
            Sample.Report() with { Tables = [Sample.Large(400)], Gantt = null }));

        Assert.All(probe.ContentStreams(), stream => Assert.Contains("(Order) Tj", stream, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_page_carries_its_number_and_the_total()
    {
        var probe = new PdfProbe(PdfReportWriter.Write(
            Sample.Report() with { Tables = [Sample.Large(400)], Gantt = null }));

        var streams = probe.ContentStreams();
        for (int page = 0; page < streams.Count; page++)
            Assert.Contains($"(Page {page + 1} of {streams.Count}) Tj", streams[page], StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_with_no_page_still_produces_a_valid_file()
    {
        // An empty page tree is invalid, so the writer inserts a blank page.
        var probe = new PdfProbe(new PdfDocumentWriter().ToArray());

        Assert.Equal(1, probe.PageCount);
        Assert.Equal("1 0 obj", probe.ObjectHeaderAt(1));
    }

    // ----- text measuring -----

    [Fact]
    public void Measuring_is_monotonic_in_length_and_in_size()
    {
        const string Text = "Cut-off saw";

        double previous = 0;
        for (int length = 1; length <= Text.Length; length++)
        {
            double width = StandardFonts.Measure(Text[..length], PdfFont.Regular, 10);
            Assert.True(width > previous, $"adding a character shrank the string at {length}");
            previous = width;
        }

        Assert.True(StandardFonts.Measure(Text, PdfFont.Regular, 20) >
                    StandardFonts.Measure(Text, PdfFont.Regular, 10));
    }

    [Fact]
    public void Measuring_scales_exactly_with_the_point_size()
    {
        Assert.Equal(
            StandardFonts.Measure("WorkPlan", PdfFont.Bold, 10) * 2.5,
            StandardFonts.Measure("WorkPlan", PdfFont.Bold, 25),
            6);
    }

    [Theory]
    [InlineData(PdfFont.Regular, ' ', 278)]
    [InlineData(PdfFont.Regular, 'A', 667)]
    [InlineData(PdfFont.Regular, 'i', 222)]
    [InlineData(PdfFont.Regular, 'W', 944)]
    [InlineData(PdfFont.Bold, ' ', 278)]
    [InlineData(PdfFont.Bold, 'A', 722)]
    [InlineData(PdfFont.Bold, 'i', 278)]
    [InlineData(PdfFont.Bold, 'W', 944)]
    public void The_width_table_matches_adobes_published_metrics(PdfFont font, char character, int expected) =>
        Assert.Equal(expected, StandardFonts.WidthOf(font, WinAnsiEncoding.Encode(character)));

    [Fact]
    public void Every_character_the_encoding_defines_has_a_width()
    {
        for (char character = ' '; character <= 'ÿ'; character++)
        {
            if (!WinAnsiEncoding.CanEncode(character))
                continue;

            Assert.True(StandardFonts.WidthOf(PdfFont.Regular, WinAnsiEncoding.Encode(character)) > 0, $"no regular width for U+{(int)character:X4}");
            Assert.True(StandardFonts.WidthOf(PdfFont.Bold, WinAnsiEncoding.Encode(character)) > 0, $"no bold width for U+{(int)character:X4}");
        }
    }

    [Fact]
    public void Bold_is_never_narrower_than_regular_for_a_letter() =>
        Assert.All("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
            letter => Assert.True(
                StandardFonts.WidthOf(PdfFont.Bold, WinAnsiEncoding.Encode(letter)) >=
                StandardFonts.WidthOf(PdfFont.Regular, WinAnsiEncoding.Encode(letter)), letter.ToString()));

    [Fact]
    public void Truncation_always_fits_the_budget_it_was_given()
    {
        const string Text = "SAW-10 — Cut-off saw with a very long descriptive name";

        for (double budget = 2; budget < 200; budget += 3.5)
        {
            var fitted = StandardFonts.Truncate(Text, PdfFont.Regular, 8, budget);
            Assert.True(StandardFonts.Measure(fitted, PdfFont.Regular, 8) <= budget + 0.0001,
                $"'{fitted}' overflows a budget of {budget}");
        }
    }

    [Fact]
    public void Text_that_fits_is_returned_untouched()
    {
        const string Text = "CNC-200";
        Assert.Equal(Text, StandardFonts.Truncate(Text, PdfFont.Regular, 8, 1000));
    }

    [Fact]
    public void Wrapping_never_exceeds_the_line_width_even_for_an_unbreakable_word()
    {
        var lines = StandardFonts.Wrap("Werkzeugmaschinenfabrik einstellbar", PdfFont.Regular, 9, 40);

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.True(StandardFonts.Measure(line, PdfFont.Regular, 9) <= 40.0001, line));
    }

    // ----- encoding -----

    [Theory]
    [InlineData('ä', 0xE4)]
    [InlineData('ö', 0xF6)]
    [InlineData('ü', 0xFC)]
    [InlineData('ß', 0xDF)]
    [InlineData('€', 0x80)]
    [InlineData('—', 0x97)]
    [InlineData('·', 0xB7)]
    public void German_text_and_the_euro_sign_map_onto_code_page_1252(char character, int expected) =>
        Assert.Equal(expected, WinAnsiEncoding.Encode(character));

    [Fact]
    public void A_character_the_code_page_has_no_glyph_for_is_replaced_visibly() =>
        Assert.Equal(WinAnsiEncoding.Replacement, WinAnsiEncoding.Encode('中'));

    [Theory]
    [InlineData("(", @"\(")]
    [InlineData(")", @"\)")]
    [InlineData(@"\", @"\\")]
    [InlineData("a(b)c", @"a\(b\)c")]
    [InlineData("plain", "plain")]
    public void The_three_characters_that_would_end_a_literal_string_are_escaped(string input, string expected) =>
        Assert.Equal(expected, Encoding.Latin1.GetString(WinAnsiEncoding.EncodeLiteral(input)));

    [Fact]
    public void A_line_break_inside_a_label_becomes_a_space_rather_than_breaking_the_stream()
    {
        var encoded = Encoding.Latin1.GetString(WinAnsiEncoding.EncodeLiteral("one\r\ntwo"));
        Assert.Equal("one  two", encoded);
    }

    [Fact]
    public void German_text_reaches_the_content_stream_as_single_bytes()
    {
        var table = new ExportTable("T", [new ExportColumn("H")], [[ExportCell.OfText("Größe 12 €")]]);
        var probe = new PdfProbe(PdfReportWriter.Write(Sample.Report() with { Tables = [table], Gantt = null }));

        // One byte per character: the euro sign is 0x80, which is exactly
        // where code page 1252 departs from Latin-1.
        Assert.Contains("(Größe 12 " + (char)0x80 + ") Tj", probe.ContentStreams()[0], StringComparison.Ordinal);
    }

    // ----- Gantt -----

    [Fact]
    public void A_chart_with_no_bars_still_draws_its_lanes_and_axis()
    {
        var gantt = new GanttExportModel("Gantt", [new GanttRow("SAW-10", [])], 3600);
        var probe = new PdfProbe(PdfReportWriter.Write(Sample.Report() with { Gantt = gantt, Tables = [] }));

        Assert.Equal(1, probe.PageCount);
        Assert.Contains("(SAW-10) Tj", probe.ContentStreams()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_chart_with_no_lanes_at_all_does_not_divide_by_zero()
    {
        var gantt = new GanttExportModel("Gantt", [], 0);
        var probe = new PdfProbe(PdfReportWriter.Write(Sample.Report() with { Gantt = gantt, Tables = [] }));

        Assert.Equal(1, probe.PageCount);
    }

    [Fact]
    public void One_bar_is_drawn_with_its_label()
    {
        var probe = new PdfProbe(PdfReportWriter.Write(Sample.Report() with { Tables = [] }));
        var stream = probe.ContentStreams()[0];

        Assert.Contains("(PO-1001) Tj", stream, StringComparison.Ordinal);
        Assert.Contains(" re f\n", stream, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bar_that_runs_past_the_axis_is_clipped_to_the_track_not_off_the_page()
    {
        var gantt = new GanttExportModel("Gantt",
            [new GanttRow("SAW-10", [new GanttBar("over", -5000, 99_000, 0, IsLate: false)])],
            3600);

        var probe = new PdfProbe(PdfReportWriter.Write(
            Sample.Report() with { Gantt = gantt, Tables = [], Headline = [] }));

        // Every rectangle in the stream stays inside the A4 landscape page:
        // "x y w h re", so the right edge is x + w.
        var rectangles = System.Text.RegularExpressions.Regex.Matches(
            probe.ContentStreams()[0], @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re");

        Assert.NotEmpty(rectangles);
        foreach (System.Text.RegularExpressions.Match rectangle in rectangles)
        {
            double x = double.Parse(rectangle.Groups[1].Value, CultureInfo.InvariantCulture);
            double width = double.Parse(rectangle.Groups[3].Value, CultureInfo.InvariantCulture);

            Assert.InRange(x, 0, 842);
            Assert.InRange(x + width, 0, 842);
        }
    }

    [Fact]
    public void Many_lanes_break_across_pages_and_each_page_repeats_the_axis()
    {
        var rows = Enumerable.Range(0, 120)
            .Select(i => new GanttRow("WC-" + i.ToString(CultureInfo.InvariantCulture),
                [new GanttBar("PO-" + i.ToString(CultureInfo.InvariantCulture), i * 60, i * 60 + 600, i % 8, IsLate: false)]))
            .ToList();

        var probe = new PdfProbe(PdfReportWriter.Write(
            Sample.Report() with { Gantt = new GanttExportModel("Gantt", rows, 8000), Tables = [] }));

        Assert.True(probe.PageCount > 1, "120 lanes should not fit on one page");
        Assert.All(probe.ContentStreams(), stream => Assert.Contains(" l S\n", stream, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3600, 3600)]                    // an hour of schedule: hourly ticks
    [InlineData(20 * 3600, 2 * 3600)]
    [InlineData(5 * 86400, 12 * 3600)]
    [InlineData(60 * 86400, 7 * 86400)]
    public void The_axis_step_keeps_the_tick_count_readable(long totalSeconds, long expected)
    {
        long step = GanttBlock.ChooseStep(totalSeconds);

        Assert.Equal(expected, step);
        Assert.True(totalSeconds / step <= 10, "too many ticks to read");
    }

    [Fact]
    public void An_axis_longer_than_a_year_still_produces_at_most_ten_ticks()
    {
        long total = 5L * 365 * 86400;
        Assert.True(total / GanttBlock.ChooseStep(total) <= 10);
    }
}
