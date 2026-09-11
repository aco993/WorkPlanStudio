using System.Globalization;
using System.Text;
using WorkPlanStudio.Export.Csv;

namespace WorkPlanStudio.Export.Tests;

/// <summary>
/// RFC 4180 is short and almost every hand-rolled CSV writer still gets it
/// wrong, usually by quoting everything or by forgetting that a quote inside a
/// quoted field is doubled rather than backslash-escaped. These pin the grammar
/// down case by case.
/// </summary>
public class CsvWriterTests
{
    private static ExportTable OneCell(ExportCell cell, ExportColumn? column = null) =>
        new("T", [column ?? new ExportColumn("H")], [[cell]]);

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("\"fully quoted\"", "\"\"\"fully quoted\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("carriage\rreturn", "\"carriage\rreturn\"")]
    [InlineData("windows\r\nbreak", "\"windows\r\nbreak\"")]
    [InlineData("", "")]
    [InlineData("semi;colon", "semi;colon")]
    public void A_field_is_quoted_exactly_when_the_grammar_requires_it(string input, string expected) =>
        Assert.Equal(expected, CsvWriter.Escape(input, ','));

    [Fact]
    public void The_separator_decides_what_has_to_be_quoted()
    {
        Assert.Equal("\"semi;colon\"", CsvWriter.Escape("semi;colon", ';'));
        Assert.Equal("has,comma", CsvWriter.Escape("has,comma", ';'));
    }

    [Fact]
    public void A_field_containing_a_windows_line_break_survives_a_round_trip()
    {
        var table = OneCell(ExportCell.OfText("first\r\nsecond"));
        var text = CsvWriter.WriteText(table, new CsvOptions { WriteByteOrderMark = false });

        // Three physical lines: the header and a record whose single field spans two.
        Assert.Equal("H\r\n\"first\r\nsecond\"\r\n", text);
    }

    [Fact]
    public void Excel_gets_a_semicolon_where_the_comma_is_the_decimal_mark()
    {
        Assert.Equal(';', CsvOptions.ForCulture(Sample.German).Separator);
        Assert.Equal(',', CsvOptions.ForCulture(Sample.English).Separator);
    }

    [Fact]
    public void The_byte_order_mark_is_present_only_when_asked_for()
    {
        var table = OneCell(ExportCell.OfText("x"));
        Assert.Equal(Encoding.UTF8.GetPreamble(), CsvWriter.Write(table).Take(3));
        Assert.Equal((byte)'H', CsvWriter.Write(table, new CsvOptions { WriteByteOrderMark = false })[0]);
    }

    [Fact]
    public void Umlauts_survive_as_utf8()
    {
        var bytes = CsvWriter.Write(OneCell(ExportCell.OfText("Größe")), new CsvOptions { WriteByteOrderMark = false });
        Assert.Equal("H\r\nGröße\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Numbers_and_dates_follow_the_injected_culture_not_the_ambient_one()
    {
        var table = new ExportTable("T",
            [new ExportColumn("N") { Format = "N1" }, new ExportColumn("D")],
            [[ExportCell.OfNumber(1234.5), ExportCell.OfDate(new DateTime(2026, 9, 11, 14, 30, 0))]]);

        var english = CsvWriter.WriteText(table, new CsvOptions { Culture = Sample.English, Separator = ';' });
        var german = CsvWriter.WriteText(table, new CsvOptions { Culture = Sample.German, Separator = ';' });

        Assert.Contains("1,234.5;11 Sept 2026 14:30", english, StringComparison.Ordinal);
        Assert.Contains("1.234,5;11. September 2026 14:30", german, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_column_never_writes_the_ambiguous_numeric_form()
    {
        // 11/09/2026 is 11 September to one reader and 9 November to another,
        // and a CSV is opened by whoever was sent it. See ExportDates.
        var table = new ExportTable("T", [new ExportColumn("D")],
            [[ExportCell.OfDate(new DateTime(2026, 9, 11, 14, 30, 0))]]);

        foreach (var culture in new[] { Sample.English, Sample.German, CultureInfo.GetCultureInfo("en-US") })
        {
            var text = CsvWriter.WriteText(table, new CsvOptions { Culture = culture, Separator = ';' });
            Assert.DoesNotContain("11/09/2026", text, StringComparison.Ordinal);
            Assert.DoesNotContain("9/11/2026", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_column_that_names_its_own_format_still_gets_it()
    {
        // The culture-coherent default is a default, not a straitjacket: a
        // caller that wants ISO for a machine to read says so.
        var table = new ExportTable("T", [new ExportColumn("D") { Format = "yyyy-MM-dd" }],
            [[ExportCell.OfDate(new DateTime(2026, 9, 11, 14, 30, 0))]]);

        Assert.Contains("2026-09-11", CsvWriter.WriteText(table, new CsvOptions { Culture = Sample.German }), StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_stamp_is_written_in_the_reports_own_language()
    {
        var german = CsvWriter.WriteReportText(Sample.Report(Sample.German), CsvOptions.ForCulture(Sample.German));
        Assert.Contains("11. September 2026 09:00", german, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+41 79 000")]
    [InlineData("-cmd")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tlead")]
    [InlineData("\rlead")]
    public void Text_a_spreadsheet_would_evaluate_is_neutralised(string payload)
    {
        var text = CsvWriter.WriteText(OneCell(ExportCell.OfText(payload)), new CsvOptions { Separator = ';' });

        Assert.Contains("'" + payload, text, StringComparison.Ordinal);
        Assert.True(FormulaGuard.LooksLikeFormula(payload));
    }

    [Fact]
    public void A_negative_number_is_not_mistaken_for_a_formula()
    {
        // The guard applies to text only: a number cell is written as a number,
        // and an apostrophe in front of it would turn it into text on import.
        var text = CsvWriter.WriteText(OneCell(ExportCell.OfNumber(-5)), new CsvOptions { Culture = CultureInfo.InvariantCulture });

        Assert.Contains("\r\n-5\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guard_can_be_turned_off_for_a_file_no_spreadsheet_will_open()
    {
        var text = CsvWriter.WriteText(OneCell(ExportCell.OfText("=1+1")), new CsvOptions { NeutraliseFormulas = false });
        Assert.Contains("\r\n=1+1\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_table_is_still_a_header_row()
    {
        Assert.Equal("Only\r\n", CsvWriter.WriteText(Sample.Empty()));
    }

    [Fact]
    public void One_column_never_emits_a_trailing_separator()
    {
        var table = new ExportTable("T", [new ExportColumn("H")], [[ExportCell.OfText("a")], [ExportCell.OfText("b")]]);
        Assert.Equal("H\r\na\r\nb\r\n", CsvWriter.WriteText(table));
    }

    [Fact]
    public void A_short_row_is_padded_rather_than_rejected()
    {
        var table = new ExportTable("T",
            [new ExportColumn("A"), new ExportColumn("B")],
            [[ExportCell.OfText("only")]]);

        Assert.Equal("A,B\r\nonly,\r\n", CsvWriter.WriteText(table));
    }

    [Fact]
    public void Ten_thousand_rows_produce_ten_thousand_and_one_lines()
    {
        var text = CsvWriter.WriteText(Sample.Large(10_000), new CsvOptions { Culture = CultureInfo.InvariantCulture });

        Assert.Equal(10_001, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("PO-9999,4999.5\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_carries_its_metadata_and_names_every_section()
    {
        var text = CsvWriter.WriteReportText(Sample.Report(), new CsvOptions { Culture = Sample.English, Separator = ';' });

        Assert.StartsWith("Production schedule\r\nSample data\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Makespan;18.5 h", text, StringComparison.Ordinal);
        Assert.Contains("\r\nJobs\r\nOrder;Part;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Booleans_are_written_as_words_a_spreadsheet_recognises()
    {
        var table = new ExportTable("T", [new ExportColumn("B")], [[ExportCell.OfBoolean(true)], [ExportCell.OfBoolean(false)]]);
        Assert.Equal("B\r\nTRUE\r\nFALSE\r\n", CsvWriter.WriteText(table));
    }
}
