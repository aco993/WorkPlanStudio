using System.Globalization;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Export;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;
using ExportGanttBar = WorkPlanStudio.Export.GanttBar;
using ExportGanttRow = WorkPlanStudio.Export.GanttRow;

namespace WorkPlanStudio.Services;

/// <summary>
/// Projects a finished schedule into the format-independent
/// <see cref="ExportReport"/> the writers consume.
/// <para>
/// The export is meant to be evidence, not a screenshot: it carries the KPI
/// figures, the jobs with their real target and completion dates, every
/// operation with the work center it ran on, the utilisation per work center
/// <em>and</em> the parameters that produced the run. A reader who disagrees
/// with the plan can see which dispatch rule, which target-date rule and which
/// seed produced it, and reproduce it.
/// </para>
/// <para>
/// It is a projection and nothing else - no database, no rendering - so the
/// whole mapping is testable against a hand-built <see cref="ScheduleResult"/>.
/// </para>
/// </summary>
public sealed class ScheduleExportBuilder
{
    private const long SecondsPerHour = 3600;

    private readonly IStringLocalizer<SharedResource> _localizer;

    /// <summary>Creates the builder. The localizer supplies every heading and label.</summary>
    public ScheduleExportBuilder(IStringLocalizer<SharedResource> localizer) => _localizer = localizer;

    /// <summary>
    /// Builds the report for one run.
    /// </summary>
    /// <param name="result">The schedule as the page has it.</param>
    /// <param name="parameters">The parameters that produced it, so the run can be repeated.</param>
    /// <param name="generatedAt">When the export was asked for.</param>
    /// <param name="culture">
    /// The culture every number and date is formatted for. Passed in rather
    /// than read from the ambient culture, so an export never quietly depends
    /// on which thread produced it.
    /// </param>
    public ExportReport Build(
        ScheduleResult result,
        SchedulingParameters parameters,
        DateTimeOffset generatedAt,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(culture);

        var metadata = new ExportMetadata(
            Text("Export_Title"),
            string.Format(culture, Text("Export_Subtitle"), DispatchLabel(parameters.DispatchRule), DueLabel(parameters.DueDateRule)),
            generatedAt,
            culture);

        var tables = new List<ExportTable> { JobsTable(result), OperationsTable(result) };
        if (result.UtilizationByWorkCenter.Count > 0)
            tables.Add(UtilizationTable(result));
        tables.Add(ParametersTable(result, parameters, generatedAt));

        return new ExportReport(metadata, Headline(result, culture), tables) { Gantt = Gantt(result) };
    }

    /// <summary>The KPI tiles, in the order the page shows them.</summary>
    private IReadOnlyList<ExportKeyValue> Headline(ScheduleResult result, CultureInfo culture)
    {
        var kpis = result.Kpis;
        return
        [
            new(Text("Sched_KpiMakespan"), Hours(kpis.MakespanSeconds, culture) + " " + Text("Export_HoursUnit")),
            new(Text("Sched_KpiOnTime"), Percent(kpis.OnTimeRate, culture)),
            new(Text("Sched_KpiTardiness"), Hours(kpis.TotalTardinessSeconds, culture) + " " + Text("Export_HoursUnit")),
            new(Text("Sched_KpiUtilization"), Percent(kpis.AverageUtilization, culture))
        ];
    }

    private ExportTable JobsTable(ScheduleResult result)
    {
        var moment = MomentColumn(result);
        var columns = new List<ExportColumn>
        {
            new(Text("Orders_Number")) { WidthCharacters = 12 },
            new(Text("Field_PartName")) { WidthCharacters = 26 },
            moment(Text("Sched_Target")),
            moment(Text("Sched_Completion")),
            new(Text("Sched_Lateness") + Suffix()) { Alignment = ExportAlignment.Right, WidthCharacters = 12, Format = "N1", ExcelNumberFormat = "0.0;[Red]-0.0" },
            new(Text("Field_Status")) { WidthCharacters = 10 }
        };

        var rows = result.Jobs
            .Select(IReadOnlyList<ExportCell> (job) =>
            [
                ExportCell.OfText(job.Reference),
                ExportCell.OfText(job.PartName),
                Moment(result, job.DueSeconds),
                Moment(result, job.CompletionSeconds),
                ExportCell.OfNumber(job.LatenessSeconds / (double)SecondsPerHour),
                ExportCell.OfText(Text(job.IsLate ? "Sched_Late" : "Sched_OnTime"))
            ])
            .ToList();

        return new ExportTable(Text("Sched_JobsTable"), columns, rows);
    }

    /// <summary>
    /// Every operation, flattened out of the Gantt lanes. This is the table a
    /// shop-floor discussion actually needs: which machine, which order, when,
    /// and how much of the time was change-over rather than cutting.
    /// </summary>
    private ExportTable OperationsTable(ScheduleResult result)
    {
        var moment = MomentColumn(result);
        var columns = new List<ExportColumn>
        {
            new(Text("Field_WorkCenter")) { WidthCharacters = 26 },
            new(Text("Orders_Number")) { WidthCharacters = 12 },
            new(Text("Field_OperationNo")) { Alignment = ExportAlignment.Right, WidthCharacters = 8, Format = "0", ExcelNumberFormat = "0" },
            moment(Text("Export_Start")),
            moment(Text("Export_End")),
            new(Text("Export_Duration") + Suffix()) { Alignment = ExportAlignment.Right, WidthCharacters = 11, Format = "N2", ExcelNumberFormat = "0.00" },
            new(Text("Sched_Setup") + Suffix()) { Alignment = ExportAlignment.Right, WidthCharacters = 11, Format = "N2", ExcelNumberFormat = "0.00" },
            new(Text("Sched_Paused") + Suffix()) { Alignment = ExportAlignment.Right, WidthCharacters = 11, Format = "N2", ExcelNumberFormat = "0.00" },
            new(Text("Sched_Late")) { Alignment = ExportAlignment.Centre, WidthCharacters = 8 }
        };

        var rows = new List<IReadOnlyList<ExportCell>>();
        foreach (var lane in result.Rows)
            foreach (var bar in lane.Bars.OrderBy(b => b.StartSeconds).ThenBy(b => b.JobReference, StringComparer.Ordinal))
                rows.Add(
                [
                    ExportCell.OfText(lane.WorkCenterName),
                    ExportCell.OfText(bar.JobReference),
                    ExportCell.OfNumber(bar.StepNumber),
                    Moment(result, bar.StartSeconds),
                    Moment(result, bar.EndSeconds),
                    ExportCell.OfNumber(bar.DurationSeconds / (double)SecondsPerHour),
                    ExportCell.OfNumber(bar.SetupSeconds / (double)SecondsPerHour),
                    ExportCell.OfNumber(bar.PausedSeconds / (double)SecondsPerHour),
                    ExportCell.OfBoolean(bar.IsLate)
                ]);

        return new ExportTable(Text("Export_Operations"), columns, rows);
    }

    private ExportTable UtilizationTable(ScheduleResult result)
    {
        var columns = new List<ExportColumn>
        {
            new(Text("Field_WorkCenter")) { WidthCharacters = 30 },
            new(Text("Sched_KpiUtilization")) { Alignment = ExportAlignment.Right, WidthCharacters = 14, Format = "P0", ExcelNumberFormat = "0 %" }
        };

        var rows = result.UtilizationByWorkCenter
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(IReadOnlyList<ExportCell> (entry) => [ExportCell.OfText(entry.Key), ExportCell.OfNumber(entry.Value)])
            .ToList();

        return new ExportTable(Text("Export_Utilization"), columns, rows);
    }

    /// <summary>
    /// The run's inputs. Without these the export is a picture; with them it is
    /// reproducible - the same parameters and seed give the same schedule.
    /// </summary>
    private ExportTable ParametersTable(
        ScheduleResult result, SchedulingParameters parameters, DateTimeOffset generatedAt)
    {
        var rows = new List<IReadOnlyList<ExportCell>>();
        void Add(string label, ExportCell value) => rows.Add([ExportCell.OfText(label), value]);

        Add(Text("Sched_DispatchRule"), ExportCell.OfText(DispatchLabel(parameters.DispatchRule)));
        Add(Text("Sched_DueRule"), ExportCell.OfText(DueLabel(parameters.DueDateRule)));
        Add(DueParameterLabel(parameters.DueDateRule), ExportCell.OfNumber(DueParameterValue(parameters)));
        Add(Text("Sched_MultiStart"), ExportCell.OfNumber(parameters.MultiStartRuns));
        Add(Text("Sched_LocalSearch"), ExportCell.OfNumber(parameters.LocalSearchMaxSteps));
        Add(Text("Export_LocalSearchTaken"), ExportCell.OfNumber(result.LocalSearchSteps));
        Add(Text("Sched_Seed"), ExportCell.OfNumber(parameters.Seed));
        Add(Text("Export_Generated"), ExportCell.OfDate(generatedAt.DateTime));

        if (result.Horizon is { } horizon)
            Add(Text("Export_Horizon"), ExportCell.OfDate(horizon));

        return new ExportTable(
            Text("Export_Parameters"),
            [
                new(Text("Export_Setting")) { WidthCharacters = 30 },
                new(Text("Export_Value")) { WidthCharacters = 30 }
            ],
            rows);
    }

    /// <summary>
    /// The Gantt lanes as the writer wants them. Colour index and lateness come
    /// straight from the view model, so a printed chart and the screen it came
    /// from agree bar for bar.
    /// </summary>
    private GanttExportModel? Gantt(ScheduleResult result)
    {
        if (!result.HasData || result.Rows.Count == 0)
            return null;

        var lanes = result.Rows
            .Select(lane => new ExportGanttRow(
                lane.WorkCenterName,
                [.. lane.Bars.Select(bar => new ExportGanttBar(bar.JobReference, bar.StartSeconds, bar.EndSeconds, bar.ColorIndex, bar.IsLate))]))
            .ToList();

        // One legend entry per job, in the order the palette was handed out, plus
        // the late marker - which is an outline, not a colour, so the chart is
        // still readable in monochrome.
        var legend = result.Jobs
            .Select(job => new GanttLegendEntry(job.Reference, job.ColorIndex))
            .ToList();
        if (result.Jobs.Any(job => job.IsLate))
            legend.Add(new GanttLegendEntry(Text("Sched_Late"), -1));

        return new GanttExportModel(Text("Sched_Gantt"), lanes, result.MakespanSeconds)
        {
            Origin = result.Horizon,
            ElapsedUnitLabel = Text("Export_HoursUnit"),
            Legend = legend
        };
    }

    // ----- cells and columns -----

    /// <summary>
    /// A point in the schedule: a real date when the run is anchored to a
    /// release date, elapsed hours when it is not. The column definition and the
    /// cells have to agree, which is why both come from here.
    /// </summary>
    private ExportCell Moment(ScheduleResult result, long seconds) =>
        result.Horizon is { } horizon
            ? ExportCell.OfDate(horizon.AddSeconds(seconds))
            : ExportCell.OfNumber(seconds / (double)SecondsPerHour);

    private Func<string, ExportColumn> MomentColumn(ScheduleResult result) =>
        result.Horizon is not null
            ? header => new ExportColumn(header) { WidthCharacters = 17 }
            : header => new ExportColumn(header + Suffix()) { Alignment = ExportAlignment.Right, WidthCharacters = 12, Format = "N1", ExcelNumberFormat = "0.0" };

    private string Suffix() => " (" + Text("Export_HoursUnit") + ")";

    // ----- labels -----

    private string Text(string key) => _localizer[key];

    /// <summary>The dispatch rule as the parameter form names it.</summary>
    public string DispatchLabel(DispatchRule rule) => rule switch
    {
        DispatchRule.Fifo => Text("Sched_Rule_Fifo"),
        DispatchRule.ShortestProcessingTime => Text("Sched_Rule_Spt"),
        DispatchRule.LongestProcessingTime => Text("Sched_Rule_Lpt"),
        DispatchRule.EarliestDueDate => Text("Sched_Rule_Edd"),
        DispatchRule.CriticalRatio => Text("Sched_Rule_Cr"),
        DispatchRule.WeightedShortestProcessingTime => Text("Sched_Rule_Wspt"),
        _ => rule.ToString()
    };

    /// <summary>The target-date rule as the parameter form names it.</summary>
    public string DueLabel(DueDateRule rule) => rule switch
    {
        DueDateRule.Explicit => Text("Sched_Due_Explicit"),
        DueDateRule.TotalWorkContent => Text("Sched_Due_Twk"),
        DueDateRule.NumberOfOperations => Text("Sched_Due_Nop"),
        DueDateRule.EqualSlack => Text("Sched_Due_Slk"),
        DueDateRule.ConstantAllowance => Text("Sched_Due_Con"),
        _ => rule.ToString()
    };

    private string DueParameterLabel(DueDateRule rule) => rule switch
    {
        DueDateRule.TotalWorkContent => Text("Sched_TwkFactor"),
        DueDateRule.NumberOfOperations => Text("Sched_NopMinutes"),
        DueDateRule.EqualSlack => Text("Sched_SlackMinutes"),
        _ => Text("Sched_ConMinutes")
    };

    private static double DueParameterValue(SchedulingParameters parameters) => parameters.DueDateRule switch
    {
        DueDateRule.TotalWorkContent => parameters.TwkFlowFactor,
        DueDateRule.NumberOfOperations => parameters.NopSecondsPerOp / 60.0,
        DueDateRule.EqualSlack => parameters.SlackSeconds / 60.0,
        _ => parameters.ConstantAllowanceSeconds / 60.0
    };

    private static string Hours(long seconds, CultureInfo culture) =>
        (seconds / (double)SecondsPerHour).ToString("N1", culture);

    private static string Percent(double rate, CultureInfo culture) => rate.ToString("P0", culture);
}
