using System.Globalization;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Export.Xlsx;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;

namespace WorkPlanStudio.Services;

/// <summary>
/// The one call the UI makes to export a schedule: project it, render it and
/// hand it to the browser. Keeping the three steps behind one method means the
/// component has no opinion about formats and the formats have no opinion about
/// Blazor.
/// </summary>
public sealed class ScheduleExportService
{
    private readonly ScheduleExportBuilder _builder;
    private readonly FileDownloadService _download;
    private readonly IStringLocalizer<SharedResource> _localizer;

    /// <summary>Creates the service.</summary>
    public ScheduleExportService(
        ScheduleExportBuilder builder,
        FileDownloadService download,
        IStringLocalizer<SharedResource> localizer)
    {
        _builder = builder;
        _download = download;
        _localizer = localizer;
    }

    /// <summary>
    /// Exports with the current moment and the current UI language. The two
    /// ambient values are read here, once, and passed on explicitly, so nothing
    /// below this point depends on them.
    /// </summary>
    public Task<string> ExportAsync(
        ScheduleResult result,
        SchedulingParameters parameters,
        ScheduleExportFormat format,
        CancellationToken cancellationToken = default) =>
        ExportAsync(result, parameters, format, DateTimeOffset.Now, CultureInfo.CurrentUICulture, cancellationToken);

    /// <summary>Exports at a given moment and in a given culture. Returns the file name the browser was given.</summary>
    /// <param name="result">The schedule to export.</param>
    /// <param name="parameters">The parameters that produced it.</param>
    /// <param name="format">PDF, workbook or CSV.</param>
    /// <param name="generatedAt">Stamped into the document and into the file name.</param>
    /// <param name="culture">The culture every value is formatted for.</param>
    /// <param name="cancellationToken">Abandons the export if the page goes away.</param>
    public async Task<string> ExportAsync(
        ScheduleResult result,
        SchedulingParameters parameters,
        ScheduleExportFormat format,
        DateTimeOffset generatedAt,
        CultureInfo culture,
        CancellationToken cancellationToken = default)
    {
        var report = _builder.Build(result, parameters, generatedAt, culture);
        var bytes = ScheduleExportFile.Render(format, report, Text());
        var fileName = ScheduleExportFile.Name(format, DateOnly.FromDateTime(generatedAt.LocalDateTime));

        await _download.SaveAsync(bytes, fileName, ScheduleExportFile.ContentType(format), cancellationToken);
        return fileName;
    }

    /// <summary>
    /// The few words the writers print themselves. Resolved here, where the
    /// localizer is, rather than inside the writers, which have no notion of a
    /// resource file and should not acquire one.
    /// </summary>
    private ScheduleExportText Text() => new(
        _localizer["Export_PageNumber"],
        new XlsxSummaryLabels(
            _localizer["Export_Summary"],
            _localizer["Export_Figure"],
            _localizer["Export_Value"],
            _localizer["Export_Generated"]),
        _localizer["Export_True"],
        _localizer["Export_False"]);
}
