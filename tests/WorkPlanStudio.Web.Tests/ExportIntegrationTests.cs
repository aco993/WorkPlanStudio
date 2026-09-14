using System.Globalization;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Export;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Services.Chat;
using SchedulePage = WorkPlanStudio.Pages.Schedule;   // disambiguate from Scheduling.Schedule

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The export seam: the projection from the page's view model into the
/// format-independent report, the menu that offers it, and the interop call
/// that hands the bytes to the browser. The three writers are tested in their
/// own project against the bytes they produce; what matters here is that the
/// app feeds them the right thing and names the result sensibly.
/// <para>
/// Every interaction with the menu goes through
/// <see cref="InteractionTestSupport.ActAsync{TComponent}(Bunit.IRenderedComponent{TComponent}, string, Action{AngleSharp.Dom.IElement})"/>,
/// which locates and triggers in one step. Doing it in two statements is a race
/// in bUnit: a re-render between the two invalidates the handler id the found
/// element carries, and the failure reads as flakiness rather than as the stale
/// element it is.
/// </para>
/// </summary>
public sealed class ExportIntegrationTests : AppBunitContext
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly DateTimeOffset Noon = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabaseFiles _files = new();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private static ScheduleExportBuilder Builder() =>
        new(new PassThroughLocalizer<SharedResource>());

    private static ExportReport Build(ScheduleResult result, SchedulingParameters? parameters = null) =>
        Builder().Build(result, parameters ?? new SchedulingParameters(), Noon, English);

    private static ExportTable TableNamed(ExportReport report, string name) =>
        report.Tables.Single(table => table.Name == name);

    // ----- the projection -----

    [Fact]
    public void The_report_carries_the_headline_figures_the_page_shows()
    {
        var report = Build(Sample.OnTime());

        Assert.Equal(
            ["Sched_KpiMakespan", "Sched_KpiOnTime", "Sched_KpiTardiness", "Sched_KpiUtilization"],
            report.Headline.Select(figure => figure.Label));

        // 1200 s of makespan is a third of an hour; the on-time rate is a percentage.
        Assert.Equal("0.3 Export_HoursUnit", report.Headline[0].Value);
        Assert.Equal("100%", report.Headline[1].Value);
    }

    [Fact]
    public void The_jobs_table_has_one_row_per_job_with_its_target_and_completion()
    {
        var table = TableNamed(Build(Sample.OnTime()), "Sched_JobsTable");

        Assert.Equal(6, table.Columns.Count);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("WP-1", table.CellAt(0, 0).Text);
        Assert.Equal("Drive shaft", table.CellAt(0, 1).Text);
        Assert.Equal("Sched_OnTime", table.CellAt(0, 5).Text);
    }

    [Fact]
    public void A_late_job_is_marked_late_and_its_lateness_is_in_hours()
    {
        var table = TableNamed(Build(Sample.WithLateJob()), "Sched_JobsTable");

        // 300 s late is a twelfth of an hour.
        Assert.Equal(300 / 3600.0, table.CellAt(0, 4).Number, 6);
        Assert.Equal("Sched_Late", table.CellAt(0, 5).Text);
    }

    [Fact]
    public void The_operations_table_has_one_row_per_bar_with_the_work_center_it_ran_on()
    {
        var table = TableNamed(Build(Sample.OnTime()), "Export_Operations");

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("SAW-10 — Cut-off Saw", table.CellAt(0, 0).Text);
        Assert.Equal("WP-1", table.CellAt(0, 1).Text);
        Assert.Equal(1, table.CellAt(0, 2).Number);
        Assert.Equal(600 / 3600.0, table.CellAt(0, 5).Number, 6);
        Assert.False(table.CellAt(0, 8).Boolean);
    }

    [Fact]
    public void Without_a_release_date_the_time_columns_are_elapsed_hours()
    {
        var table = TableNamed(Build(Sample.OnTime()), "Sched_JobsTable");

        Assert.Equal(ExportCellKind.Number, table.CellAt(0, 3).Kind);
        Assert.Contains("Export_HoursUnit", table.Columns[3].Header, StringComparison.Ordinal);
    }

    [Fact]
    public void With_a_release_date_the_time_columns_are_real_dates()
    {
        var horizon = new DateTime(2026, 6, 1, 6, 0, 0);
        var table = TableNamed(Build(Sample.OnTime() with { Horizon = horizon }), "Sched_JobsTable");

        Assert.Equal(ExportCellKind.Date, table.CellAt(0, 3).Kind);
        Assert.Equal(horizon.AddSeconds(600), table.CellAt(0, 3).Date);
        Assert.DoesNotContain("Export_HoursUnit", table.Columns[3].Header, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parameters_that_produced_the_run_travel_with_it()
    {
        var parameters = new SchedulingParameters
        {
            DispatchRule = DispatchRule.ShortestProcessingTime,
            DueDateRule = DueDateRule.TotalWorkContent,
            TwkFlowFactor = 2.5,
            MultiStartRuns = 4,
            LocalSearchMaxSteps = 1500,
            Seed = 4711
        };

        var table = TableNamed(Build(Sample.OnTime(), parameters), "Export_Parameters");
        var values = table.Rows.ToDictionary(row => row[0].Text!, row => row[1]);

        Assert.Equal("Sched_Rule_Spt", values["Sched_DispatchRule"].Text);
        Assert.Equal("Sched_Due_Twk", values["Sched_DueRule"].Text);
        Assert.Equal(2.5, values["Sched_TwkFactor"].Number);
        Assert.Equal(4, values["Sched_MultiStart"].Number);
        Assert.Equal(1500, values["Sched_LocalSearch"].Number);
        Assert.Equal(4711, values["Sched_Seed"].Number);
        Assert.Equal(Noon.DateTime, values["Export_Generated"].Date);
    }

    [Fact]
    public void The_utilization_table_appears_only_when_there_is_utilization_to_report()
    {
        Assert.DoesNotContain(Build(Sample.OnTime()).Tables, table => table.Name == "Export_Utilization");

        var withUtilization = Sample.OnTime() with
        {
            UtilizationByWorkCenter = new Dictionary<string, double> { ["SAW-10"] = 0.8, ["CNC-200"] = 0.95 }
        };
        var table = TableNamed(Build(withUtilization), "Export_Utilization");

        // Busiest first: that is the machine the reader is looking for.
        Assert.Equal("CNC-200", table.CellAt(0, 0).Text);
        Assert.Equal(0.95, table.CellAt(0, 1).Number);
    }

    [Fact]
    public void The_gantt_keeps_every_lane_bar_and_colour_from_the_page()
    {
        var gantt = Build(Sample.WithLateJob()).Gantt;

        Assert.NotNull(gantt);
        Assert.Equal(600, gantt.TotalSeconds);
        Assert.Equal("SAW-10 — Cut-off Saw", gantt.Rows[0].Label);
        Assert.Equal("WP-1", gantt.Rows[0].Bars[0].Label);
        Assert.True(gantt.Rows[0].Bars[0].IsLate);

        // One legend entry per job, plus the late marker, which is an outline
        // rather than a colour so the chart survives monochrome printing.
        Assert.Equal(["WP-1", "Sched_Late"], gantt.Legend.Select(entry => entry.Label));
        Assert.Equal(-1, gantt.Legend[^1].ColourIndex);
    }

    [Fact]
    public void An_empty_schedule_produces_a_report_with_no_chart_rather_than_a_failure()
    {
        var report = Build(ScheduleResult.Empty(480));

        Assert.Null(report.Gantt);
        Assert.Empty(TableNamed(report, "Sched_JobsTable").Rows);
    }

    // ----- file names -----

    [Theory]
    [InlineData(ScheduleExportFormat.Pdf, "workplan-schedule-2026-09-11.pdf")]
    [InlineData(ScheduleExportFormat.Excel, "workplan-schedule-2026-09-11.xlsx")]
    [InlineData(ScheduleExportFormat.Csv, "workplan-schedule-2026-09-11.csv")]
    public void The_file_name_is_dated_in_iso_order_so_a_folder_sorts_chronologically(
        ScheduleExportFormat format, string expected) =>
        Assert.Equal(expected, ScheduleExportFile.Name(format, new DateOnly(2026, 9, 11)));

    [Fact]
    public void A_file_name_never_contains_a_character_windows_refuses()
    {
        foreach (var format in Enum.GetValues<ScheduleExportFormat>())
        {
            var name = ScheduleExportFile.Name(format, new DateOnly(2026, 1, 2));
            Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain('\\', name);
        }
    }

    [Theory]
    [InlineData("a/b", "a-b")]
    [InlineData("a:b*c?", "a-b-c-")]
    [InlineData("trailing dot.", "trailing dot")]
    public void An_unsafe_name_is_made_safe_rather_than_rejected(string given, string expected) =>
        Assert.Equal(expected, ScheduleExportFile.Sanitise(given));

    [Theory]
    [InlineData(ScheduleExportFormat.Pdf, "application/pdf")]
    [InlineData(ScheduleExportFormat.Excel, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData(ScheduleExportFormat.Csv, "text/csv;charset=utf-8")]
    public void Each_format_announces_the_media_type_its_bytes_actually_are(
        ScheduleExportFormat format, string expected) =>
        Assert.Equal(expected, ScheduleExportFile.ContentType(format));

    // ----- the download -----

    [Fact]
    public async Task The_bytes_reach_javascript_with_the_file_name_and_media_type()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new FileDownloadService(JSInterop.JSRuntime);

        await service.SaveAsync([1, 2, 3], "workplan-schedule-2026-09-11.pdf", "application/pdf", Xunit.TestContext.Current.CancellationToken);

        var invocation = Assert.Single(JSInterop.Invocations[FileDownloadService.JsFunction]);
        Assert.Equal("workplan-schedule-2026-09-11.pdf", invocation.Arguments[1]);
        Assert.Equal("application/pdf", invocation.Arguments[2]);
    }

    [Fact]
    public async Task The_bytes_travel_as_a_stream_rather_than_base64()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new FileDownloadService(JSInterop.JSRuntime);

        await service.SaveAsync([1, 2, 3], "x.csv", "text/csv", Xunit.TestContext.Current.CancellationToken);

        // A base64 string through interop inflates the payload by a third and
        // marshals it twice; the stream reference hands over the raw buffer.
        var invocation = Assert.Single(JSInterop.Invocations[FileDownloadService.JsFunction]);
        Assert.IsType<Microsoft.JSInterop.DotNetStreamReference>(invocation.Arguments[0]);
    }

    [Theory]
    [InlineData(ScheduleExportFormat.Pdf)]
    [InlineData(ScheduleExportFormat.Excel)]
    [InlineData(ScheduleExportFormat.Csv)]
    public async Task Every_format_produces_a_non_empty_file_from_a_real_schedule(ScheduleExportFormat format)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ScheduleExportService(
            Builder(), new FileDownloadService(JSInterop.JSRuntime), new PassThroughLocalizer<SharedResource>());

        var name = await service.ExportAsync(
            Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) },
            new SchedulingParameters(), format, Noon, English, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(ScheduleExportFile.Name(format, new DateOnly(2026, 9, 11)), name);
    }

    // ----- one language per document -----

    /// <summary>
    /// The export library carries its own copy of the app's date rule, because it
    /// may not reference the app. This is the test that stops the copy drifting:
    /// if <see cref="Format"/> changes how a date reads on screen and
    /// <see cref="ExportDates"/> does not, the file stops matching the screen it
    /// came from and this fails.
    /// </summary>
    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void The_export_writes_a_date_exactly_as_the_screen_does(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        var moment = new DateTime(2026, 9, 11, 6, 5, 0);
        var previous = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = culture;

            Assert.Equal(Format.Date(moment), ExportDates.Date(moment, culture));
            Assert.Equal(Format.Time(moment), ExportDates.Time(moment, culture));
            Assert.Equal(Format.DateTime(moment), ExportDates.DateTime(moment, culture));
            Assert.Equal(Format.DayMonth(moment), ExportDates.DayMonth(moment, culture));
            Assert.Equal(Format.Stamp(moment), ExportDates.DayMonthTime(moment, culture));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData(ScheduleExportFormat.Pdf)]
    [InlineData(ScheduleExportFormat.Csv)]
    public void A_german_export_reads_as_german_and_an_english_one_as_english(ScheduleExportFormat format)
    {
        var result = Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) };

        var english = Text(Render(result, format, English));
        var german = Text(Render(result, format, German));

        // The month is spelled, so the two languages are distinguishable and
        // neither can be read as the other's day/month order.
        Assert.Contains("11 Sept 2026", english, StringComparison.Ordinal);
        Assert.Contains("11. September 2026", german, StringComparison.Ordinal);
        Assert.DoesNotContain("11. September 2026", english, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ScheduleExportFormat.Pdf)]
    [InlineData(ScheduleExportFormat.Csv)]
    public void No_export_prints_a_date_a_reader_could_parse_two_ways(ScheduleExportFormat format)
    {
        var result = Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) };

        foreach (var culture in new[] { English, German, CultureInfo.GetCultureInfo("en-US") })
        {
            var text = Text(Render(result, format, culture));

            Assert.DoesNotContain("01/06/2026", text, StringComparison.Ordinal);
            Assert.DoesNotContain("6/1/2026", text, StringComparison.Ordinal);
            Assert.DoesNotContain("01.06.2026", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_workbooks_summary_sheet_is_named_in_the_export_language()
    {
        // The sheet tab and its headings are the writer's own words, not the
        // report's, which is exactly why they were English in a German file.
        var bytes = Render(Sample.OnTime(), ScheduleExportFormat.Excel, German);
        var sheetNames = System.Text.Encoding.UTF8.GetString(WorkbookPart(bytes, "xl/workbook.xml"));

        Assert.Contains("Export_Summary", sheetNames, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Summary\"", sheetNames, StringComparison.Ordinal);
    }

    /// <summary>Renders a schedule through the real service seam, with the keys as copy.</summary>
    private static byte[] Render(ScheduleResult result, ScheduleExportFormat format, CultureInfo culture)
    {
        var report = Builder().Build(result, new SchedulingParameters(), Noon, culture);
        return ScheduleExportFile.Render(format, report,
            new ScheduleExportText("Export_PageNumber",
                new WorkPlanStudio.Export.Xlsx.XlsxSummaryLabels("Export_Summary", "Export_Figure", "Export_Value", "Export_Generated"),
                "Export_True", "Export_False"));
    }

    /// <summary>
    /// The readable text of a file. A PDF's literal strings are Latin-1 bytes, a
    /// CSV is UTF-8; either way what a reader sees is in there verbatim, because
    /// neither writer compresses.
    /// </summary>
    private static string Text(byte[] bytes) =>
        System.Text.Encoding.Latin1.GetString(bytes) + "\n" + System.Text.Encoding.UTF8.GetString(bytes);

    private static byte[] WorkbookPart(byte[] workbook, string path)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(workbook), System.IO.Compression.ZipArchiveMode.Read);
        using var stream = archive.GetEntry(path)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // ----- the menu -----

    private void ArrangeMenu()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton<ScheduleExportBuilder>();
        Services.AddSingleton<FileDownloadService>();
        Services.AddSingleton<ScheduleExportService>();
    }

    private IRenderedComponent<ExportMenu> RenderMenu(ScheduleResult? result = null)
    {
        ArrangeMenu();
        return Render<ExportMenu>(parameters => parameters
            .Add(menu => menu.Result, result ?? Sample.OnTime())
            .Add(menu => menu.Parameters, new SchedulingParameters()));
    }

    [Fact]
    public void The_menu_is_closed_until_it_is_opened_and_says_so()
    {
        var cut = RenderMenu();
        var trigger = cut.Find(".export-trigger");

        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
        Assert.Equal("menu", trigger.GetAttribute("aria-haspopup"));
        Assert.Equal("export-menu-panel", trigger.GetAttribute("aria-controls"));
        Assert.Empty(cut.FindAll("[role=menu]"));
    }

    [Fact]
    public async Task Clicking_the_trigger_opens_a_menu_of_the_three_formats()
    {
        var cut = RenderMenu();
        await cut.ActAsync(".export-trigger", element => element.Click());

        Assert.Equal("true", cut.Find(".export-trigger").GetAttribute("aria-expanded"));
        Assert.Equal("export-menu-panel", cut.Find("[role=menu]").Id);

        var items = cut.FindAll("[role=menuitem]");
        Assert.Equal(3, items.Count);
        Assert.Equal(["Export_Pdf", "Export_Excel", "Export_Csv"],
            items.Select(item => item.QuerySelector(".export-option-name")!.TextContent));
    }

    [Theory]
    [InlineData("ArrowDown")]
    [InlineData("ArrowUp")]
    public async Task An_arrow_key_on_the_trigger_opens_the_menu(string key)
    {
        var cut = RenderMenu();
        await cut.ActAsync(".export-trigger", element => element.KeyDown(new KeyboardEventArgs { Key = key }));

        Assert.Equal("true", cut.Find(".export-trigger").GetAttribute("aria-expanded"));
        Assert.Equal(3, cut.FindAll("[role=menuitem]").Count);
    }

    [Fact]
    public async Task Escape_closes_the_menu_again()
    {
        var cut = RenderMenu();
        await cut.ActAsync(".export-trigger", element => element.Click());
        await cut.ActAsync("[role=menu]", element => element.KeyDown(new KeyboardEventArgs { Key = "Escape" }));

        Assert.Equal("false", cut.Find(".export-trigger").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[role=menuitem]"));
    }

    [Theory]
    [InlineData("ArrowDown")]
    [InlineData("ArrowUp")]
    [InlineData("Home")]
    [InlineData("End")]
    public async Task The_arrow_keys_move_inside_the_open_menu_without_closing_it(string key)
    {
        var cut = RenderMenu();
        await cut.ActAsync(".export-trigger", element => element.Click());
        await cut.ActAsync("[role=menu]", element => element.KeyDown(new KeyboardEventArgs { Key = key }));

        Assert.Equal(3, cut.FindAll("[role=menuitem]").Count);
    }

    [Fact]
    public async Task Clicking_a_format_exports_it_and_closes_the_menu()
    {
        var cut = RenderMenu(Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) });
        await cut.ActAsync(".export-trigger", element => element.Click());
        await cut.ActAsync("[role=menuitem]", element => element.Click());

        cut.WaitForAssertion(() => Assert.Single(JSInterop.Invocations[FileDownloadService.JsFunction]));
        var invocation = JSInterop.Invocations[FileDownloadService.JsFunction].Single();

        Assert.Equal("application/pdf", invocation.Arguments[2]);
        Assert.Empty(cut.FindAll("[role=menuitem]"));
        cut.WaitForAssertion(() => Assert.Contains("Export_Ready", cut.Find("[aria-live=polite]").TextContent));
    }

    [Fact]
    public void The_menu_is_disabled_while_the_page_is_busy()
    {
        ArrangeMenu();
        var cut = Render<ExportMenu>(parameters => parameters
            .Add(menu => menu.Result, Sample.OnTime())
            .Add(menu => menu.Parameters, new SchedulingParameters())
            .Add(menu => menu.Disabled, true));

        Assert.True(cut.Find(".export-trigger").HasAttribute("disabled"));
    }

    [Fact]
    public void The_menu_is_disabled_while_the_page_has_nothing_to_export()
    {
        // Not merely inert: a control that can be pressed and does nothing is a
        // worse answer than one that says it is unavailable.
        ArrangeMenu();
        var cut = Render<ExportMenu>(parameters => parameters
            .Add(menu => menu.Result, (ScheduleResult?)null)
            .Add(menu => menu.Parameters, new SchedulingParameters()));

        Assert.True(cut.Find(".export-trigger").HasAttribute("disabled"));
    }

    // ----- the panel's geometry -----

    private static string Css => File.ReadAllText(Path.Join(RepoFiles.AppWwwroot, "css", "app.css"));

    /// <summary>
    /// Measured in Chrome at 360 px before the fix: the panel's left edge landed
    /// at -131 px and all three items were off the screen, with no horizontal
    /// scrollbar to reveal them — the head actions stack to the left margin on a
    /// narrow screen, and a panel anchored to the trigger's right edge opens the
    /// wrong way. bUnit does not lay out, so the rule itself is the assertion.
    /// </summary>
    [Fact]
    public void On_a_narrow_screen_the_panel_opens_away_from_the_edge_it_would_fall_off()
    {
        var narrow = Regex.Match(Css, @"@media\s*\(max-width:\s*480px\)\s*\{(?<body>[^}]*\.export-panel[^}]*\})");
        Assert.True(narrow.Success, "the export panel has no narrow-screen rule");

        var rule = narrow.Groups["body"].Value;
        Assert.Contains("left: 0", rule, StringComparison.Ordinal);
        Assert.Contains("right: auto", rule, StringComparison.Ordinal);

        // And it still cannot run off the other edge instead.
        Assert.Contains("width: min(260px, calc(100vw - 2rem))", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_wide_screen_the_panel_opens_leftwards_into_the_page()
    {
        // The head actions sit at the right of a wide page, so the default
        // anchoring is the trigger's right edge. Pinned because the narrow rule
        // above only makes sense as an override of it.
        var wide = Regex.Match(Css, @"\.export-panel\s*\{(?<body>[^}]*)\}");

        Assert.True(wide.Success, "the export panel has no base rule");
        Assert.Contains("right: 0", wide.Groups["body"].Value, StringComparison.Ordinal);
    }

    // ----- on the scheduling page -----

    private FakeScheduleService ArrangePage(ScheduleResult result)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var fake = new FakeScheduleService { Result = result };
        Services.AddSingleton<IProductionScheduleService>(fake);
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton<RuleBasedNarrator>();
        Services.AddSingleton<IAssistantConfig>(new FakeAssistantConfig());
        Services.AddSingleton(new HttpClient());
        Services.AddSingleton<ScheduleAssistant>();
        Services.AddSingleton<OfflineScheduleAnswerer>();
        Services.AddSingleton<ScheduleChat>();
        Services.AddScheduleExport();
        Services.AddOptimalityProver();
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("export-page.db", new FakeStorage())));
        return fake;
    }

    [Fact]
    public void The_scheduling_page_offers_the_export_beside_the_control_that_produced_the_plan()
    {
        ArrangePage(Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) });
        var cut = Render<SchedulePage>();

        // The condition belongs inside the wait. The trigger renders disabled on
        // the first paint and is enabled once the result arrives, so waiting only
        // for it to exist can observe the disabled paint on a slow runner — which
        // is what it did.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".head-actions .export-trigger")));
        cut.WaitForAssertion(() => Assert.False(cut.Find(".export-trigger").HasAttribute("disabled")));
    }

    [Fact]
    public void There_is_nothing_to_export_before_a_run_has_produced_anything()
    {
        ArrangePage(ScheduleResult.Empty(480));
        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".export-trigger")));
        Assert.True(cut.Find(".export-trigger").HasAttribute("disabled"));
    }

    [Fact]
    public async Task The_export_is_unavailable_while_a_run_is_in_flight()
    {
        // The schedule it would write is about to be replaced; offering it mid-run
        // would hand out a file of the previous plan under the new one's name.
        var fake = ArrangePage(Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) });
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.False(cut.Find(".export-trigger").HasAttribute("disabled")));

        fake.UseGate = true;
        await cut.ActAsync("#sched-generate", button => button.Click());

        cut.WaitForAssertion(() => Assert.True(cut.Find(".export-trigger").HasAttribute("disabled")));
        fake.Gate.SetResult();
        cut.WaitForAssertion(() => Assert.False(cut.Find(".export-trigger").HasAttribute("disabled")));
    }

    [Fact]
    public async Task The_export_carries_the_parameters_of_the_run_on_screen_not_the_ones_in_the_form()
    {
        // The form goes on being edited after a run. An export that named the
        // form's current settings would name settings that were never run.
        var fake = ArrangePage(Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) });
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".export-trigger")));

        // Run with one rule, then edit the form to another without running again.
        await cut.ActAsync("#sched-rule", select => select.Change(nameof(DispatchRule.ShortestProcessingTime)));
        await cut.ActAsync("#sched-generate", button => button.Click());
        cut.WaitForAssertion(() => Assert.Equal(DispatchRule.ShortestProcessingTime, fake.LastParameters!.DispatchRule));
        await cut.ActAsync("#sched-rule", select => select.Change(nameof(DispatchRule.LongestProcessingTime)));

        var menu = cut.FindComponent<ExportMenu>();
        Assert.Equal(DispatchRule.ShortestProcessingTime, menu.Instance.Parameters.DispatchRule);
    }

    [Fact]
    public async Task A_guest_may_export_what_a_guest_may_see()
    {
        // The scheduling page is readable by every persona and nothing on it is
        // gated, so the export is not either: it contains exactly what is
        // already on the screen. This test exists so that stays a decision
        // rather than an oversight.
        ArrangeMenu();
        Services.AddDemoAuthorization(WorkspaceRole.Guest);

        var cut = Render<ExportMenu>(parameters => parameters
            .Add(menu => menu.Result, Sample.OnTime())
            .Add(menu => menu.Parameters, new SchedulingParameters()));

        await cut.ActAsync(".export-trigger", element => element.Click());
        Assert.Equal(3, cut.FindAll("[role=menuitem]").Count);
    }
}
