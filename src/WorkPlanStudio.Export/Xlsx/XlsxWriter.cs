using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace WorkPlanStudio.Export.Xlsx;

/// <summary>
/// The four words the workbook writes itself rather than taking from the report.
/// They are a parameter and not a constant because a German export that opens on
/// a sheet tab reading "Summary" is not a German export.
/// </summary>
/// <param name="SheetName">Name of the leading sheet; sanitised like any other.</param>
/// <param name="FigureHeader">Heading of the left-hand column.</param>
/// <param name="ValueHeader">Heading of the right-hand column.</param>
/// <param name="GeneratedLabel">Label of the row carrying the export timestamp.</param>
public sealed record XlsxSummaryLabels(string SheetName, string FigureHeader, string ValueHeader, string GeneratedLabel)
{
    /// <summary>The English wording, so the library is usable without a resource file.</summary>
    public static XlsxSummaryLabels English { get; } = new("Summary", "Figure", "Value", "Generated");
}

/// <summary>Workbook-wide choices. The defaults produce the file a planner expects to receive.</summary>
public sealed record XlsxOptions
{
    /// <summary>Freeze the header row so it stays visible while scrolling.</summary>
    public bool FreezeHeaderRow { get; init; } = true;

    /// <summary>Put Excel's filter drop-downs on the header row.</summary>
    public bool AutoFilter { get; init; } = true;

    /// <summary>
    /// Excel number-format code for date cells whose column does not name its
    /// own. ISO order rather than a locale's, because a workbook is read in
    /// whatever locale the recipient's Excel happens to run.
    /// </summary>
    public string DateFormat { get; init; } = "yyyy\\-mm\\-dd\\ hh:mm";

    /// <summary>Document title, written to the package core properties.</summary>
    public string? Title { get; init; }

    /// <summary>Author, written to the package core properties.</summary>
    public string? Creator { get; init; }

    /// <summary>
    /// Timestamp stored in the core properties and on every zip entry. Fixed by
    /// default so the same input produces byte-identical output, which is what
    /// lets a test compare two writes.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Prefix text a spreadsheet would evaluate as a formula. See <see cref="FormulaGuard"/>.</summary>
    public bool NeutraliseFormulas { get; init; } = true;

    /// <summary>Wording of the summary sheet <see cref="XlsxWriter.WriteReport"/> puts in front of the data.</summary>
    public XlsxSummaryLabels SummaryLabels { get; init; } = XlsxSummaryLabels.English;
}

/// <summary>
/// Writes a minimal but genuinely valid SpreadsheetML workbook using nothing but
/// <see cref="ZipArchive"/> and string building.
/// <para>
/// "Minimal" is the deliberate limit: parts that carry no information for a
/// data export - charts, themes, pivot caches, calculation chains - are simply
/// absent, which Excel accepts. What is present is the part that makes a
/// spreadsheet worth more than a CSV: cells keep their type, so a date sorts
/// chronologically and a number sums, the header row freezes and filters, and
/// columns arrive at a readable width.
/// </para>
/// </summary>
public static class XlsxWriter
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    // Excel's day 1 is 1900-01-01, but it also believes 1900 was a leap year.
    // Counting from 1899-12-30 reproduces that off-by-one for every date from
    // 1900-03-01 on, which is every date this app can produce.
    private static readonly DateTime SerialEpoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ZipEnd = new(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);

    /// <summary>The MIME type of the produced file, for a download or an HTTP response.</summary>
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Writes one sheet per table. Sheet names are sanitised and made unique - see <see cref="SheetNames"/>.</summary>
    public static byte[] Write(IReadOnlyList<ExportTable> tables, XlsxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count == 0)
            throw new ArgumentException("A workbook needs at least one sheet.", nameof(tables));

        var settings = options ?? new XlsxOptions();
        var names = SheetNames.Unique(tables.Select(t => t.Name));

        var strings = new SharedStrings();
        var styles = new StyleTable(settings.DateFormat);
        var sheets = new string[tables.Count];
        for (int i = 0; i < tables.Count; i++)
            sheets[i] = Worksheet(tables[i], strings, styles, settings);

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "[Content_Types].xml", ContentTypes(tables.Count), settings);
            Add(archive, "_rels/.rels", PackageRelationships(), settings);
            Add(archive, "docProps/core.xml", CoreProperties(settings), settings);
            Add(archive, "docProps/app.xml", ApplicationProperties(), settings);
            Add(archive, "xl/workbook.xml", Workbook(names), settings);
            Add(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships(tables.Count), settings);
            Add(archive, "xl/styles.xml", styles.ToXml(), settings);
            Add(archive, "xl/sharedStrings.xml", strings.ToXml(), settings);
            for (int i = 0; i < sheets.Length; i++)
                Add(archive, $"xl/worksheets/sheet{i + 1}.xml", sheets[i], settings);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Writes a report: a summary sheet carrying the metadata and the headline
    /// figures, then one sheet per table. The summary sheet is what makes the
    /// file evidence rather than a data dump - it says which run produced it.
    /// </summary>
    public static byte[] WriteReport(ExportReport report, XlsxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var settings = options ?? new XlsxOptions();
        settings = settings with
        {
            Title = settings.Title ?? report.Metadata.Title,
            Timestamp = settings.Timestamp == new XlsxOptions().Timestamp ? report.Metadata.GeneratedAt : settings.Timestamp
        };

        var summary = SummarySheet(report, settings.SummaryLabels);
        return Write([summary, .. report.Tables], settings);
    }

    /// <summary>The sheet holding the metadata and the headline figures, exposed so it can be asserted on directly.</summary>
    /// <param name="report">The report the sheet describes.</param>
    /// <param name="labels">The sheet's own wording; English when omitted.</param>
    public static ExportTable SummarySheet(ExportReport report, XlsxSummaryLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var wording = labels ?? XlsxSummaryLabels.English;
        var rows = new List<IReadOnlyList<ExportCell>>();
        void Add(string label, ExportCell value) => rows.Add([ExportCell.OfText(label), value]);

        Add(report.Metadata.Title, ExportCell.Blank);
        if (!string.IsNullOrEmpty(report.Metadata.Subtitle))
            Add(report.Metadata.Subtitle, ExportCell.Blank);

        Add(wording.GeneratedLabel, ExportCell.OfDate(report.Metadata.GeneratedAt.DateTime));
        foreach (var figure in report.Headline)
            Add(figure.Label, ExportCell.OfText(figure.Value));

        return new ExportTable(
            wording.SheetName,
            [
                new ExportColumn(wording.FigureHeader) { WidthCharacters = 34 },
                new ExportColumn(wording.ValueHeader) { WidthCharacters = 28 }
            ],
            rows);
    }

    private static void Add(ZipArchive archive, string path, string content, XlsxOptions options)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);

        // The zip timestamp is a DOS date: it cannot express anything before
        // 1980 or after 2107, and assigning one that cannot throws.
        entry.LastWriteTime = options.Timestamp < ZipEpoch || options.Timestamp > ZipEnd ? ZipEpoch : options.Timestamp;
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Worksheet(ExportTable table, SharedStrings strings, StyleTable styles, XlsxOptions options)
    {
        int columnCount = Math.Max(1, table.Columns.Count);
        int rowCount = table.Rows.Count + 1;

        var builder = new StringBuilder(1024 + table.Rows.Count * 64 * columnCount);
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""")
               .Append("<worksheet xmlns=\"").Append(SpreadsheetNamespace).Append("\">")
               .Append("<dimension ref=\"A1:").Append(XlsxXml.CellReference(rowCount - 1, columnCount - 1)).Append("\"/>");

        builder.Append("<sheetViews><sheetView workbookViewId=\"0\">");
        if (options.FreezeHeaderRow)
            builder.Append("""<pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/><selection pane="bottomLeft" activeCell="A2" sqref="A2"/>""");
        builder.Append("</sheetView></sheetViews>");
        builder.Append("""<sheetFormatPr defaultRowHeight="15"/>""");

        if (table.Columns.Count > 0)
        {
            builder.Append("<cols>");
            for (int i = 0; i < table.Columns.Count; i++)
            {
                var width = Math.Clamp(table.Columns[i].WidthCharacters, 2, 255).ToString("0.##", CultureInfo.InvariantCulture);
                builder.Append("<col min=\"").Append(i + 1).Append("\" max=\"").Append(i + 1)
                       .Append("\" width=\"").Append(width).Append("\" customWidth=\"1\"/>");
            }

            builder.Append("</cols>");
        }

        builder.Append("<sheetData>");
        if (table.Columns.Count > 0)
        {
            builder.Append("<row r=\"1\">");
            for (int column = 0; column < table.Columns.Count; column++)
                AppendCell(builder, 0, column,
                    styles.Header(table.Columns[column].Alignment),
                    ExportCell.OfText(table.Columns[column].Header), strings, options);
            builder.Append("</row>");
        }

        for (int row = 0; row < table.Rows.Count; row++)
        {
            builder.Append("<row r=\"").Append(row + 2).Append("\">");
            for (int column = 0; column < table.Columns.Count; column++)
            {
                var cell = table.CellAt(row, column);
                if (cell.Kind == ExportCellKind.Empty)
                    continue;
                AppendCell(builder, row + 1, column, styles.Body(table.Columns[column], cell.Kind), cell, strings, options);
            }

            builder.Append("</row>");
        }

        builder.Append("</sheetData>");

        if (options.AutoFilter && table.Columns.Count > 0)
            builder.Append("<autoFilter ref=\"A1:")
                   .Append(XlsxXml.CellReference(rowCount - 1, columnCount - 1))
                   .Append("\"/>");

        return builder.Append("</worksheet>").ToString();
    }

    private static void AppendCell(
        StringBuilder builder, int row, int column, int style, ExportCell cell, SharedStrings strings, XlsxOptions options)
    {
        builder.Append("<c r=\"").Append(XlsxXml.CellReference(row, column)).Append("\" s=\"").Append(style).Append('"');

        switch (cell.Kind)
        {
            case ExportCellKind.Number:
                builder.Append("><v>").Append(cell.Number.ToString("R", CultureInfo.InvariantCulture)).Append("</v></c>");
                break;

            case ExportCellKind.Boolean:
                builder.Append(" t=\"b\"><v>").Append(cell.Boolean ? '1' : '0').Append("</v></c>");
                break;

            case ExportCellKind.Date when cell.Date >= SerialEpoch:
                builder.Append("><v>")
                       .Append((cell.Date - SerialEpoch).TotalDays.ToString("0.###########", CultureInfo.InvariantCulture))
                       .Append("</v></c>");
                break;

            case ExportCellKind.Date:
                // Excel's serial numbers do not reach back past 1900. Rather
                // than write a value it would render as ###, fall back to ISO
                // text: unsortable, but not silently wrong.
                AppendSharedString(builder, cell.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), strings, neutralise: false);
                break;

            default:
                AppendSharedString(builder, cell.Text ?? "", strings, options.NeutraliseFormulas);
                break;
        }
    }

    private static void AppendSharedString(StringBuilder builder, string text, SharedStrings strings, bool neutralise)
    {
        var value = neutralise ? FormulaGuard.Neutralise(text) : text;
        builder.Append(" t=\"s\"><v>").Append(strings.IndexOf(value)).Append("</v></c>");
    }

    private static string ContentTypes(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""")
               .Append("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""")
               .Append("""<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""")
               .Append("""<Default Extension="xml" ContentType="application/xml"/>""")
               .Append("""<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");

        for (int i = 1; i <= sheetCount; i++)
            builder.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i)
                   .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");

        return builder
            .Append("""<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""")
            .Append("""<Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>""")
            .Append("""<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>""")
            .Append("""<Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>""")
            .Append("</Types>")
            .ToString();
    }

    private static string PackageRelationships() =>
        $"""
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{PackageRelationshipNamespace}"><Relationship Id="rId1" Type="{RelationshipNamespace}/officeDocument" Target="xl/workbook.xml"/><Relationship Id="rId2" Type="{PackageRelationshipNamespace}/metadata/core-properties" Target="docProps/core.xml"/><Relationship Id="rId3" Type="{RelationshipNamespace}/extended-properties" Target="docProps/app.xml"/></Relationships>
         """;

    private static string CoreProperties(XlsxOptions options)
    {
        var stamp = options.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>{XlsxXml.Escape(options.Title ?? "")}</dc:title><dc:creator>{XlsxXml.Escape(options.Creator ?? "WorkPlan Studio")}</dc:creator><cp:lastModifiedBy>{XlsxXml.Escape(options.Creator ?? "WorkPlan Studio")}</cp:lastModifiedBy><dcterms:created xsi:type="dcterms:W3CDTF">{stamp}</dcterms:created><dcterms:modified xsi:type="dcterms:W3CDTF">{stamp}</dcterms:modified></cp:coreProperties>
                """;
    }

    private static string ApplicationProperties() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>WorkPlan Studio</Application></Properties>
        """;

    private static string Workbook(IReadOnlyList<string> names)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""")
               .Append("<workbook xmlns=\"").Append(SpreadsheetNamespace)
               .Append("\" xmlns:r=\"").Append(RelationshipNamespace).Append("\"><sheets>");

        for (int i = 0; i < names.Count; i++)
            builder.Append("<sheet name=\"").Append(XlsxXml.Escape(names[i]))
                   .Append("\" sheetId=\"").Append(i + 1)
                   .Append("\" r:id=\"rId").Append(i + 1).Append("\"/>");

        return builder.Append("</sheets></workbook>").ToString();
    }

    private static string WorkbookRelationships(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""")
               .Append("<Relationships xmlns=\"").Append(PackageRelationshipNamespace).Append("\">");

        for (int i = 1; i <= sheetCount; i++)
            builder.Append("<Relationship Id=\"rId").Append(i).Append("\" Type=\"").Append(RelationshipNamespace)
                   .Append("/worksheet\" Target=\"worksheets/sheet").Append(i).Append(".xml\"/>");

        return builder
            .Append("<Relationship Id=\"rId").Append(sheetCount + 1).Append("\" Type=\"").Append(RelationshipNamespace)
            .Append("/styles\" Target=\"styles.xml\"/>")
            .Append("<Relationship Id=\"rId").Append(sheetCount + 2).Append("\" Type=\"").Append(RelationshipNamespace)
            .Append("/sharedStrings\" Target=\"sharedStrings.xml\"/>")
            .Append("</Relationships>")
            .ToString();
    }

    /// <summary>
    /// The workbook's string pool. SpreadsheetML stores text once and cells
    /// reference it by index, which is why an export of 10 000 rows of a dozen
    /// repeated work-center names stays small.
    /// </summary>
    private sealed class SharedStrings
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly List<string> _values = [];
        private int _references;

        public int IndexOf(string value)
        {
            _references++;
            if (_index.TryGetValue(value, out var existing))
                return existing;

            _index[value] = _values.Count;
            _values.Add(value);
            return _values.Count - 1;
        }

        public string ToXml()
        {
            var builder = new StringBuilder(64 + _values.Count * 24);
            builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""")
                   .Append("<sst xmlns=\"").Append(SpreadsheetNamespace)
                   .Append("\" count=\"").Append(_references)
                   .Append("\" uniqueCount=\"").Append(_values.Count).Append("\">");

            foreach (var value in _values)
                builder.Append("<si><t xml:space=\"preserve\">").Append(XlsxXml.Escape(value)).Append("</t></si>");

            return builder.Append("</sst>").ToString();
        }
    }

    /// <summary>
    /// The cell formats the workbook uses, interned on demand. A style is fully
    /// determined by the column and the kind of value in the cell, so the same
    /// combination is registered once however many cells share it.
    /// </summary>
    private sealed class StyleTable
    {
        private const int FirstCustomNumberFormat = 164;   // 0-163 are Excel's built-ins.

        private readonly string _defaultDateFormat;
        private readonly Dictionary<string, int> _numberFormats = new(StringComparer.Ordinal);
        private readonly Dictionary<(bool Bold, int NumberFormat, ExportAlignment Alignment), int> _index = [];
        private readonly List<(bool Bold, int NumberFormat, ExportAlignment Alignment)> _formats = [];

        public StyleTable(string defaultDateFormat)
        {
            _defaultDateFormat = defaultDateFormat;
            Register(false, 0, ExportAlignment.Left);   // style 0 must be the plain default
        }

        public int Header(ExportAlignment alignment) => Register(true, 0, alignment);

        public int Body(ExportColumn column, ExportCellKind kind)
        {
            int numberFormat = kind switch
            {
                ExportCellKind.Date => NumberFormatId(column.ExcelNumberFormat ?? _defaultDateFormat),
                ExportCellKind.Number when column.ExcelNumberFormat is { } code => NumberFormatId(code),
                _ => 0
            };

            return Register(false, numberFormat, column.Alignment);
        }

        public string ToXml()
        {
            var builder = new StringBuilder();
            builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""")
                   .Append("<styleSheet xmlns=\"").Append(SpreadsheetNamespace).Append("\">");

            if (_numberFormats.Count > 0)
            {
                builder.Append("<numFmts count=\"").Append(_numberFormats.Count).Append("\">");
                foreach (var (code, id) in _numberFormats.OrderBy(pair => pair.Value))
                    builder.Append("<numFmt numFmtId=\"").Append(id).Append("\" formatCode=\"").Append(XlsxXml.Escape(code)).Append("\"/>");
                builder.Append("</numFmts>");
            }

            builder.Append("""<fonts count="2"><font><sz val="11"/><color theme="1"/><name val="Calibri"/><family val="2"/></font><font><b/><sz val="11"/><color theme="1"/><name val="Calibri"/><family val="2"/></font></fonts>""")
                   // Fills 0 and 1 are reserved by the format and must be exactly these two.
                   .Append("""<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEDEFF4"/><bgColor indexed="64"/></patternFill></fill></fills>""")
                   .Append("""<borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style="thin"><color rgb="FFB9BFCC"/></bottom><diagonal/></border></borders>""")
                   .Append("""<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>""")
                   .Append("<cellXfs count=\"").Append(_formats.Count).Append("\">");

            foreach (var (bold, numberFormat, alignment) in _formats)
            {
                builder.Append("<xf numFmtId=\"").Append(numberFormat)
                       .Append("\" fontId=\"").Append(bold ? 1 : 0)
                       .Append("\" fillId=\"").Append(bold ? 2 : 0)
                       .Append("\" borderId=\"").Append(bold ? 1 : 0)
                       .Append("\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"");

                if (numberFormat != 0)
                    builder.Append(" applyNumberFormat=\"1\"");

                if (alignment == ExportAlignment.Left)
                    builder.Append("/>");
                else
                    builder.Append(" applyAlignment=\"1\"><alignment horizontal=\"")
                           .Append(alignment == ExportAlignment.Right ? "right" : "center")
                           .Append("\"/></xf>");
            }

            return builder.Append("</cellXfs>")
                .Append("""<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>""")
                .Append("</styleSheet>")
                .ToString();
        }

        private int NumberFormatId(string code)
        {
            if (_numberFormats.TryGetValue(code, out var existing))
                return existing;

            int id = FirstCustomNumberFormat + _numberFormats.Count;
            _numberFormats[code] = id;
            return id;
        }

        private int Register(bool bold, int numberFormat, ExportAlignment alignment)
        {
            var key = (bold, numberFormat, alignment);
            if (_index.TryGetValue(key, out var existing))
                return existing;

            _index[key] = _formats.Count;
            _formats.Add(key);
            return _formats.Count - 1;
        }
    }
}
