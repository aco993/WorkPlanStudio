using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Chat;
using SchedulePage = WorkPlanStudio.Pages.Schedule;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// What the chart shows a sighted reader. A run of a week drawn across one card
/// leaves most operations a few pixels wide, and the chart used to print the order
/// number into every bar and let the edge cut it to "PO-10" or "P", with the lane
/// names cut the same way. These pin the replacement: the code of a lane never
/// cut, a label that says only what fits, a key for the letters, and a zoom that
/// gives the short operations room.
/// </summary>
public sealed class GanttReadabilityTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    private void Arrange(ScheduleResult result)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IProductionScheduleService>(new FakeScheduleService { Result = result });
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton<RuleBasedNarrator>();
        Services.AddSingleton<IAssistantConfig>(new FakeAssistantConfig());
        Services.AddSingleton(new HttpClient());
        Services.AddSingleton<ScheduleAssistant>();
        Services.AddSingleton<OfflineScheduleAnswerer>();
        Services.AddSingleton<ScheduleChat>();
        Services.AddScheduleExport();
        Services.AddOptimalityProver();
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("gantt-read.db", new FakeStorage())));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    /// <summary>Two lanes over 40 hours, one of them named without a dash.</summary>
    private static ScheduleResult TwoLanes() => Sample.OnTime() with
    {
        Horizon = new DateTime(2026, 6, 1, 6, 0, 0),
        MakespanSeconds = 40 * 3600,
        Rows =
        [
            new GanttRow("ASM-500 — Manual Assembly",
                [new GanttBar(1, "PO-1001", 0, 10, 0, 6 * 3600, IsLate: false)], []),
            new GanttRow("Paint shop",
                [new GanttBar(2, "PO-1002", 1, 20, 6 * 3600, 12 * 3600, IsLate: false)], [])
        ]
    };

    private IRenderedComponent<SchedulePage> RenderChart()
    {
        Arrange(TwoLanes());
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".gantt-bar").Count));
        // The first run is still finishing when the bars appear, and a click
        // dispatched against a render that is about to be replaced is lost.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".page-head .btn-primary[disabled]")));
        return cut;
    }

    [Fact]
    public void A_lane_shows_its_code_and_its_name_on_separate_lines_and_keeps_the_whole_in_its_title()
    {
        var cut = RenderChart();

        var lane = cut.FindAll(".gantt-row .gantt-label")[0];
        Assert.Equal("ASM-500", lane.QuerySelector(".gantt-lane-code")!.TextContent);
        Assert.Equal("Manual Assembly", lane.QuerySelector(".gantt-lane-name")!.TextContent);
        Assert.Equal("ASM-500 — Manual Assembly", lane.GetAttribute("title"));
    }

    [Theory]
    [InlineData("SAW-10 — Cut-off Saw", "SAW-10", "Cut-off Saw")]
    [InlineData("Paint shop", "Paint shop", null)]
    [InlineData(" — leading", " — leading", null)]
    public void A_name_without_the_dash_is_all_code(string name, string code, string? rest)
    {
        Assert.Equal((code, rest), GanttChart.SplitLane(name));
    }

    /// <summary>
    /// The label is the letter, the order number and the operation; CSS decides how
    /// much of it the bar's width shows. It is hidden from assistive technology,
    /// because the bar's name already says all of it.
    /// </summary>
    [Fact]
    public void A_bar_carries_its_letter_order_and_operation_as_a_label_the_name_already_covers()
    {
        var cut = RenderChart();

        var bar = cut.FindAll(".gantt-bar")[1];
        var label = bar.QuerySelector(".gantt-bar-text")!;
        Assert.Equal("true", label.GetAttribute("aria-hidden"));
        Assert.Equal("B", label.QuerySelector(".gantt-bar-letter")!.TextContent);
        Assert.Equal("PO-1002", label.QuerySelector(".gantt-bar-ref")!.TextContent);
        Assert.Contains("20", label.QuerySelector(".gantt-bar-op")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("PO-1002", bar.GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_bars_and_the_jobs_table_use_the_same_letter_for_a_job()
    {
        Assert.Equal('A', GanttChart.JobLetter(0));
        Assert.Equal('H', GanttChart.JobLetter(7));

        var cut = RenderChart();
        var key = cut.Find(".gantt-jobs");
        Assert.Equal("true", key.GetAttribute("aria-hidden"));
        foreach (var job in Sample.OnTime().Jobs)
            Assert.Contains($"{GanttChart.JobLetter(job.ColorIndex)}{job.Reference}", key.TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 40-hour run from Monday 06:00 offers the whole plan and a day, not a week:
    /// a week view of it would be the whole plan again.
    /// </summary>
    [Fact]
    public void A_run_is_offered_only_the_spans_it_does_not_fit_in()
    {
        var cut = RenderChart();

        var spans = cut.FindAll(".gantt-zoom button").Select(b => b.TextContent).ToList();
        Assert.Equal(["Ui_GanttViewAll", "Ui_GanttViewDay"], spans);
        Assert.Equal("true", cut.FindAll(".gantt-zoom button")[0].GetAttribute("aria-pressed"));
        Assert.Contains("--gantt-zoom:1;", cut.Find(".gantt").GetAttribute("style"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".gantt-period"));
    }

    [Fact]
    public async Task The_day_view_shows_one_calendar_day_with_hours_and_steps_to_the_next()
    {
        var cut = RenderChart();
        var whole = cut.FindAll(".gantt-tick").Count;

        await cut.InvokeAsync(() => cut.FindAll(".gantt-zoom button")[1].Click());

        // One day fills the card: the 40-hour run spans 40/24 of it, and the axis marks hours.
        cut.WaitForAssertion(() => Assert.Contains("--gantt-zoom:1.667;", cut.Find(".gantt").GetAttribute("style"), StringComparison.Ordinal));
        Assert.True(cut.FindAll(".gantt-tick").Count > whole, "a day should be marked more finely than the whole run");
        Assert.Equal(Format.DayMonth(new DateTime(2026, 6, 1)), cut.Find(".gantt-period-label").TextContent);
        Assert.True(cut.FindAll(".gantt-step")[0].HasAttribute("disabled"), "there is no day before the first");
        Assert.Equal(0d, LastScroll());

        await cut.InvokeAsync(() => cut.FindAll(".gantt-step")[1].Click());

        cut.WaitForAssertion(() => Assert.Equal(Format.DayMonth(new DateTime(2026, 6, 2)), cut.Find(".gantt-period-label").TextContent));
        Assert.True(cut.FindAll(".gantt-step")[1].HasAttribute("disabled"), "the run ends on Tuesday");
        // Tuesday starts 18 h into the run, but the track can only scroll until its last day-width is in view.
        Assert.Equal(1 - 24.0 / 40, LastScroll(), 3);
    }

    /// <summary>
    /// Scrolled to Tuesday, the midnight label sits on the left edge of the time axis,
    /// where a centred label lost its first half under the lane names ("ed 3 Jun").
    /// </summary>
    [Fact]
    public async Task The_label_at_the_left_edge_of_a_day_reads_from_the_edge()
    {
        Arrange(TwoLanes() with { MakespanSeconds = 96 * 3600 });
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".gantt-bar").Count));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".page-head .btn-primary[disabled]")));

        await cut.InvokeAsync(() => cut.FindAll(".gantt-zoom button").Single(b => b.TextContent == "Ui_GanttViewDay").Click());
        // Monday's window starts at midnight, six hours before the run: no tick sits on the edge.
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".gantt-period-label")));
        Assert.Empty(cut.FindAll(".gantt-tick-start"));

        await cut.InvokeAsync(() => cut.FindAll(".gantt-step")[1].Click());

        cut.WaitForAssertion(() => Assert.Equal(Format.DayMonth(new DateTime(2026, 6, 2)), cut.Find(".gantt-tick-start").TextContent));
        Assert.Single(cut.FindAll(".gantt-tick-start"));
    }

    [Fact]
    public async Task Full_screen_is_asked_of_the_browser_for_the_chart_card()
    {
        var cut = RenderChart();

        await cut.InvokeAsync(() => cut.Find(".gantt-fullscreen").Click());

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "workplanGantt.fullScreen");
    }

    private double LastScroll() =>
        Assert.IsType<double>(JSInterop.Invocations.Last(i => i.Identifier == "workplanGantt.scrollTo").Arguments[1]);
}
