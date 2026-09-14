using System.Globalization;
using System.Text;

namespace WorkPlanStudio.Export.Csv;

/// <summary>
/// How a CSV file is shaped. The defaults are the ones that make a file open
/// correctly in Excel by double-click, which is the only way most planners will
/// ever open it.
/// </summary>
public sealed record CsvOptions
{
    /// <summary>
    /// The field separator. A comma is RFC 4180; a semicolon is what Excel
    /// expects wherever the comma is the decimal mark, which is most of Europe.
    /// </summary>
    public char Separator { get; init; } = ',';

    /// <summary>
    /// Write a UTF-8 byte-order mark. Without one Excel reads a UTF-8 file as
    /// the system code page and German text arrives as mojibake; with one every
    /// other reader still copes, so it is on by default.
    /// </summary>
    public bool WriteByteOrderMark { get; init; } = true;

    /// <summary>The culture numbers and dates are formatted for. Never the ambient one - that would make the output depend on the machine.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    /// <summary>Line separator. RFC 4180 says CRLF, and Excel on Windows agrees.</summary>
    public string NewLine { get; init; } = "\r\n";

    /// <summary>
    /// Prefix text that a spreadsheet would evaluate as a formula with an
    /// apostrophe. See <see cref="FormulaGuard"/>; turn it off only for a file
    /// no spreadsheet will open.
    /// </summary>
    public bool NeutraliseFormulas { get; init; } = true;

    /// <summary>How <see cref="ExportCellKind.Boolean"/> cells are written when the column sets no format.</summary>
    public string TrueText { get; init; } = "TRUE";

    /// <summary>Counterpart of <see cref="TrueText"/>.</summary>
    public string FalseText { get; init; } = "FALSE";

    /// <summary>
    /// The options Excel expects for <paramref name="culture"/>: a semicolon
    /// wherever the comma is the decimal separator, a comma otherwise. This is
    /// the same rule Excel itself applies to its list separator, which is why a
    /// comma-separated file full of German decimals lands in one column.
    /// </summary>
    public static CsvOptions ForCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;
        return new CsvOptions
        {
            Culture = culture,
            Separator = decimalSeparator == "," ? ';' : ','
        };
    }
}

/// <summary>
/// Writes RFC 4180 CSV. Quoting is applied where the grammar requires it and
/// nowhere else, so a file of plain values stays readable in a text editor.
/// </summary>
public static class CsvWriter
{
    /// <summary>Writes one table - a header row and the body - as text.</summary>
    public static string WriteText(ExportTable table, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var settings = options ?? new CsvOptions();
        var builder = new StringBuilder();
        AppendTable(builder, table, settings);
        return builder.ToString();
    }

    /// <summary>Writes one table as UTF-8 bytes, with the byte-order mark when <see cref="CsvOptions.WriteByteOrderMark"/> is set.</summary>
    public static byte[] Write(ExportTable table, CsvOptions? options = null) =>
        Encode(WriteText(table, options), options ?? new CsvOptions());

    /// <summary>
    /// Writes a whole report: a title block, the headline figures, then every
    /// table separated by a blank line and its name. CSV has no sheets, so the
    /// sections are the only structure available - and a reader that only wants
    /// the numbers can still skip to the row it recognises.
    /// </summary>
    public static string WriteReportText(ExportReport report, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var settings = options ?? new CsvOptions();
        var builder = new StringBuilder();

        AppendRow(builder, [report.Metadata.Title], settings);
        if (!string.IsNullOrEmpty(report.Metadata.Subtitle))
            AppendRow(builder, [report.Metadata.Subtitle], settings);
        AppendRow(builder, [FormatDate(report.Metadata.GeneratedAt.DateTime, null, settings.Culture)], settings);

        if (report.Headline.Count > 0)
        {
            builder.Append(settings.NewLine);
            foreach (var figure in report.Headline)
                AppendRow(builder, [figure.Label, figure.Value], settings);
        }

        foreach (var table in report.Tables)
        {
            builder.Append(settings.NewLine);
            AppendRow(builder, [table.Name], settings);
            AppendTable(builder, table, settings);
        }

        return builder.ToString();
    }

    /// <summary>Writes a whole report as UTF-8 bytes. See <see cref="WriteReportText"/>.</summary>
    public static byte[] WriteReport(ExportReport report, CsvOptions? options = null) =>
        Encode(WriteReportText(report, options), options ?? new CsvOptions());

    /// <summary>
    /// Quotes one field the way RFC 4180 requires: only when it contains the
    /// separator, a quote or a line break, and with embedded quotes doubled.
    /// Exposed because the rule is worth testing on its own.
    /// </summary>
    public static string Escape(string field, char separator)
    {
        ArgumentNullException.ThrowIfNull(field);
        bool mustQuote = field.Contains(separator, StringComparison.Ordinal)
            || field.Contains('"', StringComparison.Ordinal)
            || field.Contains('\n', StringComparison.Ordinal)
            || field.Contains('\r', StringComparison.Ordinal);

        return mustQuote
            ? string.Concat("\"", field.Replace("\"", "\"\"", StringComparison.Ordinal), "\"")
            : field;
    }

    private static byte[] Encode(string text, CsvOptions options)
    {
        var body = Encoding.UTF8.GetBytes(text);
        if (!options.WriteByteOrderMark)
            return body;

        var bom = Encoding.UTF8.GetPreamble();
        var result = new byte[bom.Length + body.Length];
        bom.CopyTo(result, 0);
        body.CopyTo(result, bom.Length);
        return result;
    }

    private static void AppendTable(StringBuilder builder, ExportTable table, CsvOptions options)
    {
        // Headers go through the same guard as the body: a column heading is
        // data too, and one called "-Menge" is a formula to a spreadsheet.
        AppendRow(builder, table.Columns.Select(c => Guard(c.Header, options)).ToArray(), options);

        var field = new string[table.Columns.Count];
        for (int row = 0; row < table.Rows.Count; row++)
        {
            for (int column = 0; column < table.Columns.Count; column++)
                field[column] = Render(table.CellAt(row, column), table.Columns[column], options);
            AppendRow(builder, field, options);
        }
    }

    private static void AppendRow(StringBuilder builder, IReadOnlyList<string> fields, CsvOptions options)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                builder.Append(options.Separator);
            builder.Append(Escape(fields[i], options.Separator));
        }

        builder.Append(options.NewLine);
    }

    private static string Render(ExportCell cell, ExportColumn column, CsvOptions options) => cell.Kind switch
    {
        ExportCellKind.Empty => "",
        ExportCellKind.Number => cell.Number.ToString(column.Format, options.Culture),
        ExportCellKind.Date => FormatDate(cell.Date, column.Format, options.Culture),
        ExportCellKind.Boolean => cell.Boolean ? options.TrueText : options.FalseText,
        _ => Guard(cell.Text!, options)
    };

    private static string Guard(string value, CsvOptions options) =>
        options.NeutraliseFormulas ? FormulaGuard.Neutralise(value) : value;

    // Not the culture's own short pattern: both languages write that purely
    // numerically and disagree about the order. See ExportDates.
    private static string FormatDate(DateTime value, string? format, CultureInfo culture) =>
        format is null ? ExportDates.DateTime(value, culture) : value.ToString(format, culture);
}
