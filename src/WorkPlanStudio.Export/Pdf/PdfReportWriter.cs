using System.Globalization;

namespace WorkPlanStudio.Export.Pdf;

/// <summary>Report-level choices on top of <see cref="PdfDocumentOptions"/>.</summary>
public sealed record PdfReportOptions
{
    /// <summary>Sheet size before orientation.</summary>
    public PdfPageSize PageSize { get; init; } = PdfPageSize.A4;

    /// <summary>Orientation. A Gantt chart and a wide operations table both want landscape.</summary>
    public PdfPageOrientation Orientation { get; init; } = PdfPageOrientation.Landscape;

    /// <summary>Margins in points.</summary>
    public PdfMargins Margins { get; init; } = PdfMargins.All(34);

    /// <summary>Author, written to the document information dictionary.</summary>
    public string? Author { get; init; }

    /// <summary>Line printed at the foot of every page, left of the page number.</summary>
    public string? FooterNote { get; init; }

    /// <summary>Localised "Page {0} of {1}" pattern. The default keeps the writer usable without a resource file.</summary>
    public string PageNumberFormat { get; init; } = "Page {0} of {1}";
}

/// <summary>
/// Turns an <see cref="ExportReport"/> into a paginated PDF: a running header, a
/// strip of headline figures, the Gantt chart and every table, with page numbers
/// that know the total.
/// </summary>
public static class PdfReportWriter
{
    /// <summary>The MIME type of the produced file.</summary>
    public const string ContentType = PdfDocumentWriter.ContentType;

    /// <summary>Renders the report.</summary>
    public static byte[] Write(ExportReport report, PdfReportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var settings = options ?? new PdfReportOptions();
        var culture = report.Metadata.Culture;

        var writer = new PdfDocumentWriter(new PdfDocumentOptions
        {
            PageSize = settings.PageSize,
            Orientation = settings.Orientation,
            Margins = settings.Margins,
            Title = report.Metadata.Title,
            Subject = report.Metadata.Subtitle,
            Author = settings.Author,
            Language = culture.TwoLetterISOLanguageName,
            CreatedAt = report.Metadata.GeneratedAt
        });

        var layout = new PdfLayout(writer, page => DrawPageHeader(page, report, culture));

        if (report.Headline.Count > 0)
            DrawHeadline(layout, report.Headline);

        if (report.Gantt is { } gantt)
            new GanttBlock(gantt, culture).Render(layout);

        foreach (var table in report.Tables)
            new TableBlock(table, culture).Render(layout);

        DrawFooters(writer, settings, culture);
        return writer.ToArray();
    }

    private static double DrawPageHeader(PdfPage page, ExportReport report, CultureInfo culture)
    {
        double y = page.ContentTop + 11;
        page.Text(report.Metadata.Title, page.ContentLeft, y, new PdfTextStyle(PdfFont.Bold, 11, PdfColour.Text));

        // The header stamp names the run, and someone will read it off a printed
        // page months later - so it spells the month rather than numbering it.
        var stamp = ExportDates.DateTime(report.Metadata.GeneratedAt.DateTime, culture);
        page.TextRight(stamp, page.ContentRight, y, PdfTextStyle.Caption);

        if (!string.IsNullOrEmpty(report.Metadata.Subtitle))
        {
            y += 11;
            page.Text(report.Metadata.Subtitle, page.ContentLeft, y, PdfTextStyle.Caption);
        }

        y += 5;
        page.Line(page.ContentLeft, y, page.ContentRight, y, 1.2, PdfColour.Accent);
        return y + 6;
    }

    private static void DrawHeadline(PdfLayout layout, IReadOnlyList<ExportKeyValue> figures)
    {
        const double TileHeight = 34;
        const double Gap = 6;

        layout.EnsureSpace(TileHeight + 10);
        layout.Advance(6);

        double tileWidth = (layout.Width - Gap * (figures.Count - 1)) / figures.Count;
        for (int i = 0; i < figures.Count; i++)
        {
            double x = layout.Left + i * (tileWidth + Gap);
            layout.Page.FilledRect(x, layout.Y, tileWidth, TileHeight, PdfColour.Tint);
            layout.Page.Line(x, layout.Y, x, layout.Y + TileHeight, 2, PdfColour.Accent);

            var value = StandardFonts.Truncate(figures[i].Value, PdfFont.Bold, 13, tileWidth - 14);
            var label = StandardFonts.Truncate(figures[i].Label, PdfFont.Regular, 7, tileWidth - 14);
            layout.Page.Text(value, x + 8, layout.Y + 17, new PdfTextStyle(PdfFont.Bold, 13, PdfColour.Text));
            layout.Page.Text(label, x + 8, layout.Y + 28, new PdfTextStyle(PdfFont.Regular, 7, PdfColour.Muted));
        }

        layout.Advance(TileHeight + 8);
    }

    private static void DrawFooters(PdfDocumentWriter writer, PdfReportOptions options, CultureInfo culture)
    {
        // Only now is the total known, which is why the footer is drawn last
        // rather than by the page-started hook.
        for (int i = 0; i < writer.Pages.Count; i++)
        {
            var page = writer.Pages[i];

            // Inside the bottom margin, which is what a margin is for: content
            // stops at ContentBottom, so the running foot can never collide
            // with the last row of a table.
            double y = page.ContentBottom + 12;
            page.Line(page.ContentLeft, page.ContentBottom + 4, page.ContentRight, page.ContentBottom + 4, 0.5, PdfColour.Rule);

            if (!string.IsNullOrEmpty(options.FooterNote))
                page.Text(options.FooterNote, page.ContentLeft, y, PdfTextStyle.Caption);

            var number = string.Format(culture, options.PageNumberFormat, i + 1, writer.Pages.Count);
            page.TextRight(number, page.ContentRight, y, PdfTextStyle.Caption);
        }
    }
}
