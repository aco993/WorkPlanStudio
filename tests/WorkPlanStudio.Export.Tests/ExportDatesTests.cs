using System.Globalization;
using WorkPlanStudio.Export.Pdf;

namespace WorkPlanStudio.Export.Tests;

/// <summary>
/// The rule that a document reads as one language from end to end, and that no
/// date in it can be read two ways.
/// <para>
/// This is not a style preference. The defect these pin down shipped: a PDF
/// whose body was English carried axis labels reading <c>1.6. 06:00</c> and a
/// header stamp reading <c>11/09/2026</c> — one German, one ambiguous, in a
/// document a reader would take to be English throughout.
/// </para>
/// </summary>
public class ExportDatesTests
{
    private static readonly DateTime Moment = new(2026, 9, 11, 6, 5, 0);

    [Theory]
    [InlineData("en-GB", "11 Sept 2026")]
    [InlineData("en-US", "11 Sep 2026")]
    [InlineData("de-DE", "11. September 2026")]
    public void A_full_date_spells_the_month(string culture, string expected) =>
        Assert.Equal(expected, ExportDates.Date(Moment, CultureInfo.GetCultureInfo(culture)));

    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void The_clock_is_the_same_twenty_four_hour_one_in_every_language(string culture)
    {
        // Shift rosters and machine logs do not use am/pm, and an export is read
        // against them.
        Assert.Equal("06:05", ExportDates.Time(Moment, CultureInfo.GetCultureInfo(culture)));
        Assert.DoesNotContain("AM", ExportDates.DateTime(Moment, CultureInfo.GetCultureInfo(culture)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en-GB", "Fri 11 Sept")]
    [InlineData("de-DE", "Fr 11.9.")]
    public void A_compact_day_carries_the_weekday_that_makes_it_readable(string culture, string expected) =>
        Assert.Equal(expected, ExportDates.DayMonth(Moment, CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void A_german_axis_tick_is_german_and_an_english_one_is_english()
    {
        Assert.Equal("Fr 11.9. 06:05", ExportDates.DayMonthTime(Moment, Sample.German));
        Assert.Equal("Fri 11 Sept 06:05", ExportDates.DayMonthTime(Moment, Sample.English));
    }

    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void No_rendering_produces_a_date_a_reader_could_parse_two_ways(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        var rendered = new[]
        {
            ExportDates.Date(Moment, culture),
            ExportDates.DateTime(Moment, culture),
            ExportDates.DayMonth(Moment, culture),
            ExportDates.DayMonthTime(Moment, culture)
        };

        // 11/09 and 9/11 are the same day written by two readers who disagree;
        // 11.9. only in company of a weekday, which DayMonth always supplies.
        Assert.All(rendered, text =>
        {
            Assert.DoesNotContain("11/09", text, StringComparison.Ordinal);
            Assert.DoesNotContain("9/11", text, StringComparison.Ordinal);
            Assert.DoesNotContain("09/11", text, StringComparison.Ordinal);
        });
    }

    // ----- what the writers do with it -----

    [Fact]
    public void An_english_pdf_labels_its_axis_in_english()
    {
        var probe = new PdfProbe(PdfReportWriter.Write(Sample.Report(Sample.English) with { Tables = [] }));
        var stream = probe.ContentStreams()[0];

        Assert.Contains("(Fri 11 Sept 06:00) Tj", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("(11.9. 06:00) Tj", stream, StringComparison.Ordinal);
    }

    [Fact]
    public void A_german_pdf_labels_its_axis_in_german()
    {
        var probe = new PdfProbe(PdfReportWriter.Write(Sample.Report(Sample.German) with { Tables = [] }));
        Assert.Contains("(Fr 11.9. 06:00) Tj", probe.ContentStreams()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_stamp_follows_the_reports_culture_rather_than_the_thread()
    {
        var english = new PdfProbe(PdfReportWriter.Write(Sample.Report(Sample.English)));
        var german = new PdfProbe(PdfReportWriter.Write(Sample.Report(Sample.German)));

        Assert.Contains("(11 Sept 2026 09:00) Tj", english.ContentStreams()[0], StringComparison.Ordinal);
        Assert.Contains("(11. September 2026 09:00) Tj", german.ContentStreams()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_axis_of_elapsed_hours_is_labelled_in_the_documents_own_words()
    {
        var gantt = new GanttExportModel("Gantt", [new GanttRow("SAW-10", [])], 7200) { ElapsedUnitLabel = "Std." };
        var probe = new PdfProbe(PdfReportWriter.Write(Sample.Report(Sample.German) with { Gantt = gantt, Tables = [] }));

        Assert.Contains("(Std.) Tj", probe.ContentStreams()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_in_a_table_cell_follows_the_same_rule_as_the_axis()
    {
        var table = new ExportTable("T", [new ExportColumn("Due")],
            [[ExportCell.OfDate(new DateTime(2026, 9, 11, 14, 30, 0))]]);

        Assert.Equal("11 Sept 2026 14:30", TableBlock.Render(table.CellAt(0, 0), table.Columns[0], Sample.English));
        Assert.Equal("11. September 2026 14:30", TableBlock.Render(table.CellAt(0, 0), table.Columns[0], Sample.German));
    }
}
