using System.Globalization;

namespace WorkPlanStudio.Export.Pdf;

/// <summary>How a Gantt chart looks on the page.</summary>
public sealed record GanttStyle
{
    /// <summary>Height of one lane in points.</summary>
    public double RowHeight { get; init; } = 15;

    /// <summary>Height of a bar inside its lane.</summary>
    public double BarHeight { get; init; } = 10;

    /// <summary>Text size for lane labels and bar labels.</summary>
    public double FontSize { get; init; } = 7;

    /// <summary>Largest share of the width the lane labels may take.</summary>
    public double LabelShare { get; init; } = 0.22;

    /// <summary>Size of the chart's caption, or 0 to leave it out.</summary>
    public double CaptionSize { get; init; } = 10.5;
}

/// <summary>
/// Draws a <see cref="GanttExportModel"/>: a time axis with tick labels, one
/// lane per resource, bars with labels, and a legend.
/// <para>
/// Lateness is drawn as a dark outline and repeated in the legend rather than
/// signalled by fill colour alone, so the chart survives being printed in
/// monochrome or read by someone who cannot separate red from green.
/// </para>
/// </summary>
public sealed class GanttBlock : IPdfBlock
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;

    // Steps a reader can do arithmetic with, from a quarter of a shift up to a
    // fortnight. The first one that yields at most ten ticks wins.
    private static readonly long[] CandidateSteps =
    [
        Hour, 2 * Hour, 3 * Hour, 6 * Hour, 12 * Hour, Day, 2 * Day, 7 * Day, 14 * Day, 28 * Day
    ];

    private readonly GanttExportModel _model;
    private readonly CultureInfo _culture;
    private readonly GanttStyle _style;

    /// <summary>Prepares the chart for drawing.</summary>
    /// <param name="model">The lanes, bars and axis length.</param>
    /// <param name="culture">The culture axis labels are formatted for.</param>
    /// <param name="style">Optional appearance overrides.</param>
    public GanttBlock(GanttExportModel model, CultureInfo culture, GanttStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(culture);
        _model = model;
        _culture = culture;
        _style = style ?? new GanttStyle();
    }

    /// <inheritdoc />
    public void Render(PdfLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        double labelWidth = Math.Min(140, layout.Width * _style.LabelShare);
        double trackLeft = layout.Left + labelWidth;
        double trackWidth = layout.Right - trackLeft;
        long axis = Math.Max(1, _model.TotalSeconds);
        long step = ChooseStep(axis);

        layout.EnsureSpace(CaptionHeight() + AxisHeight() + _style.RowHeight * 2);
        DrawCaption(layout);
        DrawAxis(layout, trackLeft, trackWidth, axis, step);

        foreach (var row in _model.Rows)
        {
            if (layout.EnsureSpace(_style.RowHeight))
                DrawAxis(layout, trackLeft, trackWidth, axis, step);

            DrawRow(layout, row, labelWidth, trackLeft, trackWidth, axis, step);
        }

        DrawLegend(layout);
        layout.Advance(10);
    }

    /// <summary>
    /// The axis step for a span, exposed because the choice is the difference
    /// between a readable axis and forty overlapping labels.
    /// </summary>
    public static long ChooseStep(long totalSeconds)
    {
        foreach (var step in CandidateSteps)
            if (totalSeconds / step <= 10)
                return step;

        // Longer than a year: fall back to a whole number of 28-day blocks.
        return 28 * Day * (long)Math.Ceiling(totalSeconds / (10.0 * 28 * Day));
    }

    private double CaptionHeight() => _style.CaptionSize > 0 ? _style.CaptionSize + 8 : 0;

    private double AxisHeight() => _style.FontSize + 8;

    private void DrawCaption(PdfLayout layout)
    {
        if (_style.CaptionSize <= 0)
            return;

        layout.Advance(_style.CaptionSize);
        layout.Page.Text(_model.Title, layout.Left, layout.Y, new PdfTextStyle(PdfFont.Bold, _style.CaptionSize, PdfColour.Text));
        layout.Advance(8);
    }

    private void DrawAxis(PdfLayout layout, double trackLeft, double trackWidth, long axis, long step)
    {
        var style = new PdfTextStyle(PdfFont.Regular, _style.FontSize, PdfColour.Muted);
        layout.Advance(_style.FontSize);

        layout.Page.Text(AxisUnitLabel(), layout.Left, layout.Y, style);
        for (long tick = 0; tick <= axis; tick += step)
        {
            double x = trackLeft + trackWidth * tick / axis;
            var label = TickLabel(tick);
            // The last label would hang off the right edge; pull it back inside.
            double width = style.Measure(label);
            layout.Page.Text(label, Math.Min(x, trackLeft + trackWidth - width), layout.Y, style);
        }

        layout.Advance(4);
        layout.Page.Line(trackLeft, layout.Y, trackLeft + trackWidth, layout.Y, 0.7, PdfColour.Rule);
        layout.Advance(4);
    }

    private void DrawRow(PdfLayout layout, GanttRow row, double labelWidth, double trackLeft, double trackWidth, long axis, long step)
    {
        var page = layout.Page;
        var labelStyle = new PdfTextStyle(PdfFont.Regular, _style.FontSize, PdfColour.Text);
        double baseline = layout.Y + _style.RowHeight / 2 + _style.FontSize / 2 - 1;

        page.Text(StandardFonts.Truncate(row.Label, labelStyle.Font, labelStyle.Size, labelWidth - 6),
            layout.Left, baseline, labelStyle);

        // Grid lines first, so the bars sit on top of them.
        for (long tick = step; tick < axis; tick += step)
        {
            double x = trackLeft + trackWidth * tick / axis;
            page.Line(x, layout.Y, x, layout.Y + _style.RowHeight, 0.4, PdfColour.Rule);
        }

        double barTop = layout.Y + (_style.RowHeight - _style.BarHeight) / 2;
        foreach (var bar in row.Bars)
        {
            // A bar may start before or end after the axis - an operation that
            // overran the horizon is still a fact. Clip it rather than drop it.
            long start = Math.Clamp(bar.StartSeconds, 0, axis);
            long end = Math.Clamp(bar.EndSeconds, start, axis);

            double x = trackLeft + trackWidth * start / axis;
            double width = Math.Max(0.8, trackWidth * (end - start) / axis);
            var fill = PdfColour.FromPalette(bar.ColourIndex);

            if (bar.IsLate)
                page.FilledRect(x, barTop, width, _style.BarHeight, fill, PdfColour.Late, 1.1);
            else
                page.FilledRect(x, barTop, width, _style.BarHeight, fill);

            var labelStyleOnBar = new PdfTextStyle(PdfFont.Bold, _style.FontSize - 0.5, PdfColour.White);
            var fitted = StandardFonts.Truncate(bar.Label, labelStyleOnBar.Font, labelStyleOnBar.Size, width - 4);
            if (fitted.Length > 0 && fitted != "…")
                page.Text(fitted, x + 2, barTop + _style.BarHeight - 2.6, labelStyleOnBar);
        }

        layout.Advance(_style.RowHeight);
    }

    private void DrawLegend(PdfLayout layout)
    {
        if (_model.Legend.Count == 0)
            return;

        var style = new PdfTextStyle(PdfFont.Regular, _style.FontSize, PdfColour.Muted);
        layout.EnsureSpace(_style.FontSize + 10);
        layout.Advance(10);

        double x = layout.Left;
        double baseline = layout.Y;
        foreach (var entry in _model.Legend)
        {
            double width = 12 + style.Measure(entry.Label) + 10;
            if (x + width > layout.Right)
            {
                x = layout.Left;
                layout.Advance(_style.FontSize + 3);
                baseline = layout.Y;
            }

            if (entry.ColourIndex < 0)
                layout.Page.FilledRect(x, baseline - _style.FontSize + 1.5, 8, 7, PdfColour.White, PdfColour.Late, 1.1);
            else
                layout.Page.FilledRect(x, baseline - _style.FontSize + 1.5, 8, 7, PdfColour.FromPalette(entry.ColourIndex));

            layout.Page.Text(entry.Label, x + 12, baseline, style);
            x += width;
        }
    }

    private string AxisUnitLabel() => _model.Origin is null ? _model.ElapsedUnitLabel : "";

    private string TickLabel(long seconds)
    {
        if (_model.Origin is not { } origin)
            return (seconds / (double)Hour).ToString("0.#", _culture);

        // A tick at midnight is a day, so it is labelled as one; a tick inside a
        // day needs the clock as well. Both go through ExportDates, so an axis
        // in an English document never reads "1.6." - which is what it used to.
        var moment = origin.AddSeconds(seconds);
        return moment.TimeOfDay == TimeSpan.Zero
            ? ExportDates.DayMonth(moment, _culture)
            : ExportDates.DayMonthTime(moment, _culture);
    }
}
