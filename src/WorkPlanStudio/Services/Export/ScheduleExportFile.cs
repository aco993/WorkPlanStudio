using System.Globalization;
using System.Text;
using WorkPlanStudio.Export.Csv;
using WorkPlanStudio.Export.Pdf;
using WorkPlanStudio.Export.Xlsx;

namespace WorkPlanStudio.Services;

/// <summary>The three formats the schedule can leave the browser in.</summary>
public enum ScheduleExportFormat
{
    /// <summary>A paginated report: KPI figures, the Gantt chart and the tables.</summary>
    Pdf,

    /// <summary>A workbook with one sheet per table, cells still typed.</summary>
    Excel,

    /// <summary>A single CSV file, for whatever the recipient's tooling is.</summary>
    Csv
}

/// <summary>
/// The words a writer prints on its own account, in the language the export is
/// being produced in. There are only a few, and they are the ones most easily
/// forgotten: a page number and a sheet tab are not part of the data, so nothing
/// in the report carries them.
/// </summary>
/// <param name="PageNumberFormat">Localised "Page {0} of {1}" for the PDF footer.</param>
/// <param name="SummaryLabels">Wording of the workbook's leading summary sheet.</param>
/// <param name="TrueText">
/// What a yes/no column says for yes in a CSV. Localised because it is not
/// prose but a literal the recipient's spreadsheet parses: German Excel reads
/// WAHR as a boolean and TRUE as text, and the German file is already addressed
/// to it by its semicolons.
/// </param>
/// <param name="FalseText">Counterpart of <paramref name="TrueText"/>.</param>
public sealed record ScheduleExportText(
    string PageNumberFormat,
    XlsxSummaryLabels SummaryLabels,
    string TrueText,
    string FalseText);

/// <summary>
/// Names and media types for an exported file.
/// <para>
/// The file name is part of the deliverable: a planner exports the same
/// schedule on several days and needs to be able to tell the downloads apart
/// without opening them, so the date is in the name, in ISO order so a
/// directory listing sorts chronologically. It also has to survive being saved
/// on Windows, which rejects a longer list of characters than the web does.
/// </para>
/// </summary>
public static class ScheduleExportFile
{
    /// <summary>The stem every exported schedule shares.</summary>
    public const string Stem = "workplan-schedule";

    // The characters Windows refuses in a file name, plus the ones a browser or
    // a shell would read as path structure.
    private static readonly char[] Unsafe =
        [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>The extension, without the dot.</summary>
    public static string Extension(ScheduleExportFormat format) => format switch
    {
        ScheduleExportFormat.Pdf => "pdf",
        ScheduleExportFormat.Excel => "xlsx",
        _ => "csv"
    };

    /// <summary>The media type the download is announced with.</summary>
    public static string ContentType(ScheduleExportFormat format) => format switch
    {
        ScheduleExportFormat.Pdf => PdfReportWriter.ContentType,
        ScheduleExportFormat.Excel => XlsxWriter.ContentType,
        _ => "text/csv;charset=utf-8"
    };

    /// <summary>For example <c>workplan-schedule-2026-09-11.pdf</c>.</summary>
    public static string Name(ScheduleExportFormat format, DateOnly date) =>
        Sanitise($"{Stem}-{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.{Extension(format)}");

    /// <summary>
    /// Replaces everything a file system would refuse with a hyphen and trims
    /// the trailing dots and spaces Windows silently drops.
    /// </summary>
    public static string Sanitise(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var builder = new StringBuilder(candidate.Length);
        foreach (var character in candidate)
            builder.Append(Array.IndexOf(Unsafe, character) >= 0 || char.IsControl(character) ? '-' : character);

        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        return cleaned.Length == 0 ? Stem : cleaned;
    }

    /// <summary>Renders <paramref name="report"/> in <paramref name="format"/>.</summary>
    /// <param name="format">Which writer to use.</param>
    /// <param name="report">The data, already formatted for its culture.</param>
    /// <param name="text">
    /// The handful of words the writers supply themselves rather than taking
    /// from the report - the PDF page number, the workbook's summary sheet.
    /// Passed in rather than looked up here so this stays free of the localizer,
    /// and so those words cannot silently fall back to English while the rest of
    /// the document is German.
    /// </param>
    public static byte[] Render(ScheduleExportFormat format, WorkPlanStudio.Export.ExportReport report, ScheduleExportText text)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(text);
        return format switch
        {
            ScheduleExportFormat.Pdf => PdfReportWriter.Write(report, new PdfReportOptions
            {
                Author = "WorkPlan Studio",
                FooterNote = report.Metadata.Subtitle,
                PageNumberFormat = text.PageNumberFormat
            }),
            ScheduleExportFormat.Excel => XlsxWriter.WriteReport(report, new XlsxOptions
            {
                SummaryLabels = text.SummaryLabels
            }),
            _ => CsvWriter.WriteReport(report, CsvOptions.ForCulture(report.Metadata.Culture) with
            {
                TrueText = text.TrueText,
                FalseText = text.FalseText
            })
        };
    }
}
