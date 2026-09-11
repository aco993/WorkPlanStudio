using System.Globalization;

namespace WorkPlanStudio.Export;

/// <summary>
/// What a cell holds. The distinction is not cosmetic: it decides whether a
/// spreadsheet can sort, sum and filter the column, and whether the value is
/// formatted for a human (CSV, PDF) or handed over as a raw number (xlsx).
/// </summary>
public enum ExportCellKind
{
    /// <summary>An empty cell. Written as nothing, never as "0" or "null".</summary>
    Empty,

    /// <summary>Free text. The only kind that can carry a formula-injection payload.</summary>
    Text,

    /// <summary>A number. Stays a number in the spreadsheet, so SUM works.</summary>
    Number,

    /// <summary>A point in time. Stays a date in the spreadsheet, so sorting is chronological.</summary>
    Date,

    /// <summary>A yes/no value.</summary>
    Boolean
}

/// <summary>Horizontal alignment of a column, honoured by the PDF table and the xlsx cell style.</summary>
public enum ExportAlignment
{
    /// <summary>Left, the default for text.</summary>
    Left,

    /// <summary>Right, the usual choice for numbers so the digits line up.</summary>
    Right,

    /// <summary>Centred.</summary>
    Centre
}

/// <summary>
/// One value in a table. A struct because a schedule export is tens of thousands
/// of these and none of them outlives the write.
/// </summary>
public readonly record struct ExportCell
{
    private ExportCell(ExportCellKind kind, string? text, double number, DateTime date, bool boolean)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Date = date;
        Boolean = boolean;
    }

    /// <summary>Which of the value properties carries the payload.</summary>
    public ExportCellKind Kind { get; }

    /// <summary>The text, for <see cref="ExportCellKind.Text"/>; otherwise <c>null</c>.</summary>
    public string? Text { get; }

    /// <summary>The number, for <see cref="ExportCellKind.Number"/>.</summary>
    public double Number { get; }

    /// <summary>The moment, for <see cref="ExportCellKind.Date"/>.</summary>
    public DateTime Date { get; }

    /// <summary>The flag, for <see cref="ExportCellKind.Boolean"/>.</summary>
    public bool Boolean { get; }

    /// <summary>An empty cell.</summary>
    public static ExportCell Blank { get; } = new(ExportCellKind.Empty, null, 0, default, false);

    /// <summary>A text cell. A <c>null</c> or empty string collapses to <see cref="Blank"/>.</summary>
    public static ExportCell OfText(string? value) =>
        string.IsNullOrEmpty(value) ? Blank : new(ExportCellKind.Text, value, 0, default, false);

    /// <summary>A numeric cell.</summary>
    public static ExportCell OfNumber(double value) => new(ExportCellKind.Number, null, value, default, false);

    /// <summary>A numeric cell from a decimal, widened once here rather than at every call site.</summary>
    public static ExportCell OfNumber(decimal value) => OfNumber((double)value);

    /// <summary>
    /// A numeric cell from a whole number. Present so an integer argument picks
    /// an overload instead of being ambiguous between double and decimal - and
    /// counts are the commonest thing a report puts in a number column.
    /// </summary>
    public static ExportCell OfNumber(long value) => OfNumber((double)value);

    /// <summary>A date/time cell.</summary>
    public static ExportCell OfDate(DateTime value) => new(ExportCellKind.Date, null, 0, value, false);

    /// <summary>A boolean cell.</summary>
    public static ExportCell OfBoolean(bool value) => new(ExportCellKind.Boolean, null, 0, default, value);
}

/// <summary>
/// A column: its heading, how wide it wants to be and how its values are
/// formatted. One width serves both writers - it is measured in characters,
/// which is the unit Excel's column width uses and a good enough proxy for the
/// share of the page a PDF column should take.
/// </summary>
/// <param name="Header">The heading shown in the first row.</param>
public sealed record ExportColumn(string Header)
{
    /// <summary>Alignment of the body cells. Headings follow the body.</summary>
    public ExportAlignment Alignment { get; init; } = ExportAlignment.Left;

    /// <summary>Preferred width in characters; also the column's share of the PDF table width.</summary>
    public double WidthCharacters { get; init; } = 14;

    /// <summary>
    /// .NET format string used when the value has to become text - CSV and PDF.
    /// Left <c>null</c> the value uses the culture's default, which is what a
    /// date usually wants and a measured quantity usually does not.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Excel number-format code (for example <c>0.0</c>) applied to the column's
    /// cells in the workbook. Null means Excel's General format. Kept separate
    /// from <see cref="Format"/> because the two grammars are not the same and
    /// pretending otherwise produces a file Excel repairs on open.
    /// </summary>
    public string? ExcelNumberFormat { get; init; }
}

/// <summary>
/// A rectangular block of values with a name. Rows shorter than
/// <see cref="Columns"/> are padded with blanks rather than rejected, so a
/// caller may leave a trailing optional column off.
/// </summary>
/// <param name="Name">Sheet name in the workbook, section heading in CSV and PDF.</param>
/// <param name="Columns">The column definitions, left to right.</param>
/// <param name="Rows">The body rows.</param>
public sealed record ExportTable(
    string Name,
    IReadOnlyList<ExportColumn> Columns,
    IReadOnlyList<IReadOnlyList<ExportCell>> Rows)
{
    /// <summary>The cell at <paramref name="row"/>/<paramref name="column"/>, or blank when the row is short.</summary>
    public ExportCell CellAt(int row, int column)
    {
        var cells = Rows[row];
        return column < cells.Count ? cells[column] : ExportCell.Blank;
    }
}

/// <summary>One headline figure, rendered as a KPI tile in the PDF and a row elsewhere.</summary>
/// <param name="Label">What it measures.</param>
/// <param name="Value">The already-formatted value; the caller owns the culture.</param>
public sealed record ExportKeyValue(string Label, string Value);

/// <summary>
/// Who produced the file and when. Written into the PDF document information
/// dictionary and the workbook core properties, and printed on the first page,
/// so an exported schedule can be traced back to the run that made it.
/// </summary>
/// <param name="Title">Document title.</param>
/// <param name="Subtitle">Optional second line.</param>
/// <param name="GeneratedAt">When the export was produced.</param>
/// <param name="Culture">The culture every value in the report was formatted for.</param>
public sealed record ExportMetadata(string Title, string? Subtitle, DateTimeOffset GeneratedAt, CultureInfo Culture);

/// <summary>One bar on a Gantt row.</summary>
/// <param name="Label">Drawn inside the bar when it fits, truncated when it does not.</param>
/// <param name="StartSeconds">Start, seconds from the model's zero point.</param>
/// <param name="EndSeconds">Exclusive end, seconds from the model's zero point.</param>
/// <param name="ColourIndex">Index into the renderer's palette; the same job keeps the same colour.</param>
/// <param name="IsLate">Drawn with a warning outline, so lateness is not signalled by colour alone.</param>
public sealed record GanttBar(string Label, long StartSeconds, long EndSeconds, int ColourIndex, bool IsLate)
{
    /// <summary>Length in seconds. Negative durations are the caller's problem; the renderer clamps them to zero.</summary>
    public long DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>One Gantt lane - a resource and the bars placed on it.</summary>
/// <param name="Label">The resource name shown to the left of the track.</param>
/// <param name="Bars">The bars, in any order.</param>
public sealed record GanttRow(string Label, IReadOnlyList<GanttBar> Bars);

/// <summary>A legend entry: a palette colour and what it stands for.</summary>
/// <param name="Label">The text beside the swatch.</param>
/// <param name="ColourIndex">Index into the renderer's palette, or -1 for the "late" marker.</param>
public sealed record GanttLegendEntry(string Label, int ColourIndex);

/// <summary>
/// A Gantt chart reduced to what a page needs: lanes, bars and the length of
/// the time axis. Wall-clock dates are optional - without an
/// <see cref="Origin"/> the axis is labelled in elapsed hours, which is what
/// the app shows when it has no release date to anchor to.
/// </summary>
/// <param name="Title">Heading above the chart.</param>
/// <param name="Rows">The lanes, top to bottom.</param>
/// <param name="TotalSeconds">Length of the axis. Bars beyond it are clipped, not dropped.</param>
public sealed record GanttExportModel(string Title, IReadOnlyList<GanttRow> Rows, long TotalSeconds)
{
    /// <summary>The wall-clock moment second 0 corresponds to, when there is one.</summary>
    public DateTime? Origin { get; init; }

    /// <summary>
    /// The unit the axis is labelled with when there is no <see cref="Origin"/>
    /// and the ticks are elapsed hours. Localised by the caller: an axis reading
    /// "h" in an otherwise German document is the same defect as a date in the
    /// wrong order, only smaller.
    /// </summary>
    public string ElapsedUnitLabel { get; init; } = "h";

    /// <summary>What the colours mean. Empty hides the legend.</summary>
    public IReadOnlyList<GanttLegendEntry> Legend { get; init; } = [];
}

/// <summary>
/// One export, in a form all three writers understand. Each renders it in its
/// own idiom - the workbook puts every table on its own sheet, the CSV
/// concatenates them with section headings, the PDF draws the chart and
/// paginates the tables - but none of them needs to know where the data came
/// from.
/// </summary>
/// <param name="Metadata">Title, subtitle, timestamp and culture.</param>
/// <param name="Headline">The KPI figures, already formatted.</param>
/// <param name="Tables">The tables, in reading order.</param>
public sealed record ExportReport(
    ExportMetadata Metadata,
    IReadOnlyList<ExportKeyValue> Headline,
    IReadOnlyList<ExportTable> Tables)
{
    /// <summary>The chart, drawn by the PDF writer only. The other formats have no place for it.</summary>
    public GanttExportModel? Gantt { get; init; }
}
