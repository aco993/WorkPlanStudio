using System.Globalization;

namespace WorkPlanStudio.Export.Pdf;

/// <summary>How a table looks on the page.</summary>
public sealed record TableStyle
{
    /// <summary>Body text size in points.</summary>
    public double FontSize { get; init; } = 8;

    /// <summary>Height of one row in points.</summary>
    public double RowHeight { get; init; } = 13;

    /// <summary>Horizontal padding inside a cell.</summary>
    public double CellPadding { get; init; } = 4;

    /// <summary>Size of the table's caption, or 0 to leave the caption out.</summary>
    public double CaptionSize { get; init; } = 10.5;

    /// <summary>
    /// Shade every other row. It is a reading aid on a wide table and the only
    /// decoration here; it carries no meaning, so nothing is lost in
    /// monochrome.
    /// </summary>
    public bool ZebraRows { get; init; } = true;
}

/// <summary>
/// Draws an <see cref="ExportTable"/>, breaking across pages and repeating the
/// header row on each - a table whose second page has no headings is a table
/// nobody can read.
/// </summary>
public sealed class TableBlock : IPdfBlock
{
    private readonly ExportTable _table;
    private readonly CultureInfo _culture;
    private readonly TableStyle _style;

    /// <summary>Prepares the table for drawing.</summary>
    /// <param name="table">The data.</param>
    /// <param name="culture">The culture numbers and dates are formatted for.</param>
    /// <param name="style">Optional appearance overrides.</param>
    public TableBlock(ExportTable table, CultureInfo culture, TableStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(culture);
        _table = table;
        _culture = culture;
        _style = style ?? new TableStyle();
    }

    /// <inheritdoc />
    public void Render(PdfLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (_table.Columns.Count == 0)
            return;

        var edges = ColumnEdges(layout.Width, layout.Left);

        // A caption with no room for its own header and one row under it belongs
        // on the next page, not stranded at the foot of this one.
        layout.EnsureSpace(CaptionHeight() + _style.RowHeight * 2);
        DrawCaption(layout);
        DrawHeader(layout, edges);

        for (int row = 0; row < _table.Rows.Count; row++)
        {
            if (layout.EnsureSpace(_style.RowHeight))
                DrawHeader(layout, edges);

            DrawRow(layout, edges, row);
        }

        layout.Page.Line(layout.Left, layout.Y, layout.Right, layout.Y, 0.5, PdfColour.Rule);
        layout.Advance(10);
    }

    /// <summary>
    /// The text a cell shows. Public because the PDF and the CSV must agree on
    /// what a value looks like, and the rule is worth asserting on directly.
    /// </summary>
    public static string Render(ExportCell cell, ExportColumn column, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(culture);
        return cell.Kind switch
        {
            ExportCellKind.Empty => "",
            ExportCellKind.Number => cell.Number.ToString(column.Format, culture),
            // Not the culture's short pattern: it is numeric in both languages
            // and means different days in each. See ExportDates.
            ExportCellKind.Date => column.Format is null
                ? ExportDates.DateTime(cell.Date, culture)
                : cell.Date.ToString(column.Format, culture),
            ExportCellKind.Boolean => cell.Boolean ? "•" : "",
            // No formula guard here: a PDF cannot evaluate anything, and a
            // leading apostrophe on the page would just be wrong.
            _ => cell.Text ?? ""
        };
    }

    private double CaptionHeight() => _style.CaptionSize > 0 ? _style.CaptionSize + 8 : 0;

    private void DrawCaption(PdfLayout layout)
    {
        if (_style.CaptionSize <= 0)
            return;

        layout.Advance(_style.CaptionSize);
        layout.Page.Text(_table.Name, layout.Left, layout.Y, new PdfTextStyle(PdfFont.Bold, _style.CaptionSize, PdfColour.Text));
        layout.Advance(8);
    }

    private double[] ColumnEdges(double width, double left)
    {
        double total = _table.Columns.Sum(column => Math.Max(1, column.WidthCharacters));
        var edges = new double[_table.Columns.Count + 1];
        edges[0] = left;
        for (int i = 0; i < _table.Columns.Count; i++)
            edges[i + 1] = edges[i] + width * Math.Max(1, _table.Columns[i].WidthCharacters) / total;

        return edges;
    }

    private void DrawHeader(PdfLayout layout, double[] edges)
    {
        var style = new PdfTextStyle(PdfFont.Bold, _style.FontSize, PdfColour.Text);
        layout.Page.FilledRect(edges[0], layout.Y, edges[^1] - edges[0], _style.RowHeight, PdfColour.Tint);

        double baseline = layout.Y + _style.RowHeight - (_style.RowHeight - _style.FontSize) / 2 - 1;
        for (int column = 0; column < _table.Columns.Count; column++)
            DrawCellText(layout, edges, column, _table.Columns[column].Header, style, baseline);

        layout.Advance(_style.RowHeight);
        layout.Page.Line(edges[0], layout.Y, edges[^1], layout.Y, 0.7, PdfColour.Rule);
    }

    private void DrawRow(PdfLayout layout, double[] edges, int row)
    {
        if (_style.ZebraRows && row % 2 == 1)
            layout.Page.FilledRect(edges[0], layout.Y, edges[^1] - edges[0], _style.RowHeight, PdfColour.Tint);

        var style = new PdfTextStyle(PdfFont.Regular, _style.FontSize, PdfColour.Text);
        double baseline = layout.Y + _style.RowHeight - (_style.RowHeight - _style.FontSize) / 2 - 1;
        for (int column = 0; column < _table.Columns.Count; column++)
        {
            var text = Render(_table.CellAt(row, column), _table.Columns[column], _culture);
            DrawCellText(layout, edges, column, text, style, baseline);
        }

        layout.Advance(_style.RowHeight);
    }

    private void DrawCellText(PdfLayout layout, double[] edges, int column, string text, PdfTextStyle style, double baseline)
    {
        double left = edges[column] + _style.CellPadding;
        double right = edges[column + 1] - _style.CellPadding;
        var fitted = StandardFonts.Truncate(text, style.Font, style.Size, right - left);
        layout.Page.TextAligned(fitted, left, right, baseline, style, _table.Columns[column].Alignment);
    }
}
