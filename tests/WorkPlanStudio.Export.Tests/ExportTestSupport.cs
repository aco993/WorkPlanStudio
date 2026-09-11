using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace WorkPlanStudio.Export.Tests;

/// <summary>Hand-built inputs shared by the writer tests.</summary>
internal static class Sample
{
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");
    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static ExportTable Jobs() => new(
        "Jobs",
        [
            new ExportColumn("Order") { WidthCharacters = 12 },
            new ExportColumn("Part") { WidthCharacters = 24 },
            new ExportColumn("Hours") { Alignment = ExportAlignment.Right, Format = "N1", ExcelNumberFormat = "0.0" },
            new ExportColumn("Due"),
            new ExportColumn("Late") { Alignment = ExportAlignment.Centre }
        ],
        [
            [ExportCell.OfText("PO-1001"), ExportCell.OfText("Drive shaft"), ExportCell.OfNumber(12.5), ExportCell.OfDate(new DateTime(2026, 9, 11, 14, 30, 0)), ExportCell.OfBoolean(false)],
            [ExportCell.OfText("PO-1002"), ExportCell.OfText("Bracket, welded"), ExportCell.OfNumber(3.25), ExportCell.OfDate(new DateTime(2026, 9, 12, 6, 0, 0)), ExportCell.OfBoolean(true)]
        ]);

    public static ExportTable Empty() => new("Empty", [new ExportColumn("Only")], []);

    public static ExportReport Report(CultureInfo? culture = null) => new(
        new ExportMetadata("Production schedule", "Sample data", new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero), culture ?? English),
        [new ExportKeyValue("Makespan", "18.5 h"), new ExportKeyValue("On time", "50 %")],
        [Jobs()])
    {
        Gantt = Gantt()
    };

    public static GanttExportModel Gantt() => new(
        "Gantt",
        [
            new GanttRow("SAW-10 — Cut-off saw", [new GanttBar("PO-1001", 0, 3600, 0, IsLate: false)]),
            new GanttRow("CNC-200 — Turning", [new GanttBar("PO-1002", 3600, 9000, 1, IsLate: true)])
        ],
        9000)
    {
        Origin = new DateTime(2026, 9, 11, 6, 0, 0),
        Legend = [new GanttLegendEntry("PO-1001", 0), new GanttLegendEntry("Late", -1)]
    };

    /// <summary>A table of <paramref name="rows"/> rows, for the volume tests.</summary>
    public static ExportTable Large(int rows)
    {
        var body = new List<IReadOnlyList<ExportCell>>(rows);
        for (int i = 0; i < rows; i++)
            body.Add([ExportCell.OfText("PO-" + i.ToString(CultureInfo.InvariantCulture)), ExportCell.OfNumber(i * 0.5)]);

        return new ExportTable("Large", [new ExportColumn("Order"), new ExportColumn("Value")], body);
    }
}

/// <summary>
/// Reads a produced workbook back: the entry names, the shared strings and the
/// cells of a sheet. A test that only checks the writer did not throw proves
/// nothing about whether Excel would open the result.
/// </summary>
internal sealed class WorkbookProbe
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private readonly Dictionary<string, string> _parts = new(StringComparer.Ordinal);

    public WorkbookProbe(byte[] workbook)
    {
        using var archive = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            _parts[entry.FullName] = reader.ReadToEnd();
        }
    }

    public IReadOnlyCollection<string> EntryNames => _parts.Keys;

    public string Part(string name) => _parts.TryGetValue(name, out var content)
        ? content
        : throw new InvalidOperationException($"the package has no part '{name}'; it has: {string.Join(", ", _parts.Keys)}");

    public XDocument Xml(string name) => XDocument.Parse(Part(name));

    public IReadOnlyList<string> SharedStrings() =>
        [.. Xml("xl/sharedStrings.xml").Root!.Elements(Main + "si").Select(si => si.Element(Main + "t")?.Value ?? "")];

    public IReadOnlyList<string> SheetNames() =>
        [.. Xml("xl/workbook.xml").Root!.Element(Main + "sheets")!.Elements(Main + "sheet").Select(s => s.Attribute("name")!.Value)];

    public XElement Sheet(int oneBasedIndex) => Xml($"xl/worksheets/sheet{oneBasedIndex}.xml").Root!;

    public IReadOnlyList<XElement> Cells(int sheet) =>
        [.. Sheet(sheet).Element(Main + "sheetData")!.Elements(Main + "row").SelectMany(r => r.Elements(Main + "c"))];

    public XElement Cell(int sheet, string reference) =>
        Cells(sheet).FirstOrDefault(c => c.Attribute("r")?.Value == reference)
        ?? throw new InvalidOperationException($"sheet {sheet} has no cell {reference}");

    /// <summary>
    /// Rebuilds the visible grid of a sheet: shared strings resolved, everything
    /// else as the raw value. This is the round-trip the tests measure against.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Grid(int sheet)
    {
        var strings = SharedStrings();
        var grid = new List<IReadOnlyList<string>>();
        foreach (var row in Sheet(sheet).Element(Main + "sheetData")!.Elements(Main + "row"))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                var value = cell.Element(Main + "v")?.Value ?? "";
                cells.Add(cell.Attribute("t")?.Value == "s" ? strings[int.Parse(value, CultureInfo.InvariantCulture)] : value);
            }

            grid.Add(cells);
        }

        return grid;
    }
}
