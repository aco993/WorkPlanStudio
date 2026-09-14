using System.Globalization;
using System.Xml.Linq;
using WorkPlanStudio.Export.Xlsx;

namespace WorkPlanStudio.Export.Tests;

/// <summary>
/// A workbook is a zip of XML parts that have to agree with one another: a
/// content type for every part, a relationship for every reference, a shared
/// string for every index. Excel reports any disagreement as "unreadable
/// content" and repairs the file, which is exactly the failure a test has to
/// catch before a user does - so these unzip the result and read the XML.
/// </summary>
public class XlsxWriterTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void The_package_contains_every_part_the_format_requires()
    {
        var probe = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()]));

        Assert.Contains("[Content_Types].xml", probe.EntryNames);
        Assert.Contains("_rels/.rels", probe.EntryNames);
        Assert.Contains("xl/workbook.xml", probe.EntryNames);
        Assert.Contains("xl/_rels/workbook.xml.rels", probe.EntryNames);
        Assert.Contains("xl/worksheets/sheet1.xml", probe.EntryNames);
        Assert.Contains("xl/styles.xml", probe.EntryNames);
        Assert.Contains("xl/sharedStrings.xml", probe.EntryNames);
    }

    [Fact]
    public void Every_part_that_needs_a_content_type_has_one()
    {
        var bytes = XlsxWriter.Write([Sample.Jobs(), Sample.Empty()]);
        var probe = new WorkbookProbe(bytes);
        var contentTypes = probe.Part("[Content_Types].xml");

        foreach (var part in probe.EntryNames.Where(name => name != "[Content_Types].xml" && !name.EndsWith(".rels", StringComparison.Ordinal)))
            Assert.Contains("/" + part, contentTypes, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_sheet_has_a_relationship_and_a_worksheet_part()
    {
        var probe = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs(), Sample.Empty()]));
        var relationships = probe.Part("xl/_rels/workbook.xml.rels");

        Assert.Equal(["Jobs", "Empty"], probe.SheetNames());
        Assert.Contains("Target=\"worksheets/sheet1.xml\"", relationships, StringComparison.Ordinal);
        Assert.Contains("Target=\"worksheets/sheet2.xml\"", relationships, StringComparison.Ordinal);
        Assert.Contains("Target=\"styles.xml\"", relationships, StringComparison.Ordinal);
        Assert.Contains("Target=\"sharedStrings.xml\"", relationships, StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_strings_count_the_references_and_the_distinct_values()
    {
        var repeated = new ExportTable("R",
            [new ExportColumn("A"), new ExportColumn("B")],
            [
                [ExportCell.OfText("same"), ExportCell.OfText("same")],
                [ExportCell.OfText("same"), ExportCell.OfText("other")]
            ]);

        var probe = new WorkbookProbe(XlsxWriter.Write([repeated]));
        var sst = probe.Xml("xl/sharedStrings.xml").Root!;

        // Two headers plus four body cells; four distinct values: A, B, same, other.
        Assert.Equal("6", sst.Attribute("count")!.Value);
        Assert.Equal("4", sst.Attribute("uniqueCount")!.Value);
        Assert.Equal(["A", "B", "same", "other"], probe.SharedStrings());
    }

    [Fact]
    public void Every_shared_string_index_a_cell_uses_resolves()
    {
        var probe = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()]));
        int available = probe.SharedStrings().Count;

        var indices = probe.Cells(1)
            .Where(c => c.Attribute("t")?.Value == "s")
            .Select(c => int.Parse(c.Element(Main + "v")!.Value, CultureInfo.InvariantCulture));

        Assert.All(indices, index => Assert.InRange(index, 0, available - 1));
    }

    [Fact]
    public void A_cell_keeps_the_type_that_makes_the_column_sortable()
    {
        var probe = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()]));

        // Text is a shared string, a number carries no type attribute at all,
        // a date is a number with a date format, a boolean is t="b".
        Assert.Equal("s", probe.Cell(1, "A2").Attribute("t")?.Value);
        Assert.Null(probe.Cell(1, "C2").Attribute("t"));
        Assert.Equal("12.5", probe.Cell(1, "C2").Element(Main + "v")!.Value);
        Assert.Null(probe.Cell(1, "D2").Attribute("t"));
        Assert.Equal("b", probe.Cell(1, "E2").Attribute("t")?.Value);
        Assert.Equal("0", probe.Cell(1, "E2").Element(Main + "v")!.Value);
    }

    [Fact]
    public void A_date_is_a_serial_number_with_a_date_format()
    {
        var probe = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()]));
        var cell = probe.Cell(1, "D2");

        // 2026-09-11 14:30 is 46276 days after 1899-12-30, plus 14.5 hours.
        Assert.Equal(46276.604, double.Parse(cell.Element(Main + "v")!.Value, CultureInfo.InvariantCulture), 3);

        int styleIndex = int.Parse(cell.Attribute("s")!.Value, CultureInfo.InvariantCulture);
        var styles = probe.Xml("xl/styles.xml").Root!;
        var format = styles.Element(Main + "cellXfs")!.Elements(Main + "xf").ElementAt(styleIndex).Attribute("numFmtId")!.Value;

        Assert.Contains(styles.Element(Main + "numFmts")!.Elements(Main + "numFmt"),
            numFmt => numFmt.Attribute("numFmtId")!.Value == format && numFmt.Attribute("formatCode")!.Value.Contains("yyyy", StringComparison.Ordinal));
    }

    [Fact]
    public void A_date_before_the_serial_epoch_falls_back_to_text_rather_than_a_wrong_number()
    {
        var table = new ExportTable("Old", [new ExportColumn("D")], [[ExportCell.OfDate(new DateTime(1850, 4, 1))]]);
        var probe = new WorkbookProbe(XlsxWriter.Write([table]));

        Assert.Equal("s", probe.Cell(1, "A2").Attribute("t")?.Value);
        Assert.Contains("1850-04-01 00:00", probe.SharedStrings());
    }

    [Fact]
    public void The_header_row_is_frozen_and_filterable()
    {
        var sheet = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()])).Sheet(1);

        var pane = sheet.Element(Main + "sheetViews")!.Element(Main + "sheetView")!.Element(Main + "pane")!;
        Assert.Equal("1", pane.Attribute("ySplit")!.Value);
        Assert.Equal("frozen", pane.Attribute("state")!.Value);
        Assert.Equal("A1:E3", sheet.Element(Main + "autoFilter")!.Attribute("ref")!.Value);
    }

    [Fact]
    public void Both_can_be_turned_off()
    {
        var sheet = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()], new XlsxOptions { FreezeHeaderRow = false, AutoFilter = false })).Sheet(1);

        Assert.Null(sheet.Element(Main + "sheetViews")!.Element(Main + "sheetView")!.Element(Main + "pane"));
        Assert.Null(sheet.Element(Main + "autoFilter"));
    }

    [Fact]
    public void Column_widths_reach_the_sheet()
    {
        var columns = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()])).Sheet(1).Element(Main + "cols")!.Elements(Main + "col").ToList();

        Assert.Equal(5, columns.Count);
        Assert.Equal("12", columns[0].Attribute("width")!.Value);
        Assert.Equal("24", columns[1].Attribute("width")!.Value);
    }

    [Fact]
    public void The_header_row_is_bold()
    {
        var probe = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()]));
        int headerStyle = int.Parse(probe.Cell(1, "A1").Attribute("s")!.Value, CultureInfo.InvariantCulture);
        int bodyStyle = int.Parse(probe.Cell(1, "A2").Attribute("s")!.Value, CultureInfo.InvariantCulture);

        var styles = probe.Xml("xl/styles.xml").Root!;
        var cellXfs = styles.Element(Main + "cellXfs")!.Elements(Main + "xf").ToList();
        var fonts = styles.Element(Main + "fonts")!.Elements(Main + "font").ToList();

        int headerFont = int.Parse(cellXfs[headerStyle].Attribute("fontId")!.Value, CultureInfo.InvariantCulture);
        int bodyFont = int.Parse(cellXfs[bodyStyle].Attribute("fontId")!.Value, CultureInfo.InvariantCulture);

        Assert.NotNull(fonts[headerFont].Element(Main + "b"));
        Assert.Null(fonts[bodyFont].Element(Main + "b"));
    }

    [Fact]
    public void The_cell_format_count_matches_the_formats_written()
    {
        var styles = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()])).Xml("xl/styles.xml").Root!;
        var cellXfs = styles.Element(Main + "cellXfs")!;

        Assert.Equal(cellXfs.Elements(Main + "xf").Count().ToString(CultureInfo.InvariantCulture), cellXfs.Attribute("count")!.Value);
    }

    [Theory]
    [InlineData("a & b")]
    [InlineData("<tag>")]
    [InlineData("quote \" here")]
    [InlineData("Größe · Prüfung")]
    public void Text_that_would_break_the_xml_survives_intact(string payload)
    {
        var table = new ExportTable("T", [new ExportColumn("H")], [[ExportCell.OfText(payload)]]);
        var probe = new WorkbookProbe(XlsxWriter.Write([table]));

        Assert.Contains(payload, probe.SharedStrings());
    }

    [Fact]
    public void A_control_character_is_dropped_rather_than_corrupting_the_part()
    {
        var table = new ExportTable("T", [new ExportColumn("H")], [[ExportCell.OfText("before" + (char)0x01 + "after")]]);
        var probe = new WorkbookProbe(XlsxWriter.Write([table]));

        Assert.Contains("beforeafter", probe.SharedStrings());
    }

    [Theory]
    [InlineData(1900, 3, 1, 61)]        // the first date after Excel's phantom 29 February 1900
    [InlineData(2000, 1, 1, 36526)]
    [InlineData(2026, 9, 11, 46276)]
    public void A_date_serial_matches_the_number_excel_itself_uses(int year, int month, int day, int expected)
    {
        var table = new ExportTable("D", [new ExportColumn("When")], [[ExportCell.OfDate(new DateTime(year, month, day))]]);
        var probe = new WorkbookProbe(XlsxWriter.Write([table]));

        Assert.Equal(expected.ToString(CultureInfo.InvariantCulture), probe.Cell(1, "A2").Element(Main + "v")!.Value);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("@evil")]
    public void Text_a_spreadsheet_would_evaluate_is_neutralised(string payload)
    {
        var table = new ExportTable("T", [new ExportColumn("H")], [[ExportCell.OfText(payload)]]);
        var probe = new WorkbookProbe(XlsxWriter.Write([table]));

        Assert.Contains("'" + payload, probe.SharedStrings());
    }

    [Theory]
    [InlineData("Saw / Mill", "Saw   Mill")]
    [InlineData("a[b]c:d*e?f", "a b c d e f")]
    [InlineData("'quoted'", "quoted")]
    public void An_illegal_sheet_name_is_sanitised_rather_than_rejected(string given, string expected) =>
        Assert.Equal(expected, SheetNames.Sanitise(given));

    [Fact]
    public void A_sheet_name_is_truncated_to_the_thirty_one_characters_excel_allows()
    {
        var sanitised = SheetNames.Sanitise(new string('x', 40));

        Assert.Equal(31, sanitised.Length);
        Assert.True(SheetNames.IsValid(sanitised));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void A_name_with_nothing_to_salvage_is_rejected(string? given) =>
        Assert.Throws<ArgumentException>(() => SheetNames.Sanitise(given));

    [Fact]
    public void Duplicate_sheet_names_are_made_unique_because_excel_compares_them_case_insensitively()
    {
        // The suffix is added to the name as given: the collision is decided
        // case-insensitively, but nobody's sheet gets silently renamed in case.
        Assert.Equal(["Jobs", "jobs (2)", "JOBS (3)"], SheetNames.Unique(["Jobs", "jobs", "JOBS"]));
    }

    [Fact]
    public void A_duplicate_at_the_length_limit_still_fits()
    {
        var name = new string('y', 31);
        var unique = SheetNames.Unique([name, name]);

        Assert.All(unique, n => Assert.True(SheetNames.IsValid(n), n));
        Assert.Equal(2, unique.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Two_sheets_that_would_collide_are_written_under_different_names()
    {
        var probe = new WorkbookProbe(XlsxWriter.Write(
        [
            new ExportTable("Work / Centres", [new ExportColumn("A")], []),
            new ExportTable("Work ? Centres", [new ExportColumn("A")], [])
        ]));

        Assert.Equal(2, probe.SheetNames().Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_grid_read_back_out_of_the_zip_is_the_grid_that_went_in()
    {
        var probe = new WorkbookProbe(XlsxWriter.Write([Sample.Jobs()]));
        var grid = probe.Grid(1);

        Assert.Equal(["Order", "Part", "Hours", "Due", "Late"], grid[0]);
        Assert.Equal("PO-1001", grid[1][0]);
        Assert.Equal("Drive shaft", grid[1][1]);
        Assert.Equal("12.5", grid[1][2]);
        Assert.Equal("PO-1002", grid[2][0]);
        Assert.Equal("3.25", grid[2][2]);
    }

    [Fact]
    public void An_empty_table_still_produces_a_header_row()
    {
        var grid = new WorkbookProbe(XlsxWriter.Write([Sample.Empty()])).Grid(1);

        Assert.Single(grid);
        Assert.Equal(["Only"], grid[0]);
    }

    [Fact]
    public void A_workbook_with_no_sheet_is_refused_because_excel_would_refuse_it() =>
        Assert.Throws<ArgumentException>(() => XlsxWriter.Write([]));

    [Fact]
    public void Ten_thousand_rows_arrive_as_ten_thousand_rows()
    {
        var grid = new WorkbookProbe(XlsxWriter.Write([Sample.Large(10_000)])).Grid(1);

        Assert.Equal(10_001, grid.Count);
        Assert.Equal("PO-9999", grid[10_000][0]);
    }

    [Fact]
    public void The_same_input_produces_the_same_bytes()
    {
        Assert.Equal(XlsxWriter.Write([Sample.Jobs()]), XlsxWriter.Write([Sample.Jobs()]));
    }

    [Fact]
    public void A_report_leads_with_a_summary_sheet_naming_the_run()
    {
        var probe = new WorkbookProbe(XlsxWriter.WriteReport(Sample.Report()));

        Assert.Equal("Summary", probe.SheetNames()[0]);
        Assert.Equal("Jobs", probe.SheetNames()[1]);
        Assert.Contains("Production schedule", probe.SharedStrings());
        Assert.Contains("Makespan", probe.SharedStrings());
        Assert.Contains("Production schedule", probe.Part("docProps/core.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_sheet_speaks_the_language_the_export_was_asked_for()
    {
        // The sheet tab and its two column headings are the only words the
        // workbook writes on its own account. A German export whose first tab
        // reads "Summary" is not a German export.
        var labels = new XlsxSummaryLabels("Zusammenfassung", "Kennzahl", "Wert", "Erstellt");
        var probe = new WorkbookProbe(XlsxWriter.WriteReport(Sample.Report(Sample.German), new XlsxOptions { SummaryLabels = labels }));

        Assert.Equal("Zusammenfassung", probe.SheetNames()[0]);
        Assert.Equal(["Kennzahl", "Wert"], probe.Grid(1)[0]);
        Assert.Contains("Erstellt", probe.SharedStrings());
        Assert.DoesNotContain("Generated", probe.SharedStrings());
    }
}
