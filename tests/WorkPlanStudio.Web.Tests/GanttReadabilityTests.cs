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

    [Fact]
    public void The_chart_opens_fitted_and_zooming_in_widens_it_and_adds_ticks()
    {
        var cut = RenderChart();

        var zoom = cut.FindAll(".gantt-zoom button");
        Assert.Equal(3, zoom.Count);
        Assert.Equal("true", zoom[0].GetAttribute("aria-pressed"));
        Assert.Contains("--gantt-zoom:1", cut.Find(".gantt").GetAttribute("style"), StringComparison.Ordinal);
        var fitted = cut.FindAll(".gantt-tick").Count;

        cut.FindAll(".gantt-zoom button")[2].Click();

        Assert.Contains("--gantt-zoom:4", cut.Find(".gantt").GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal("true", cut.FindAll(".gantt-zoom button")[2].GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.FindAll(".gantt-zoom button")[0].GetAttribute("aria-pressed"));
        Assert.True(cut.FindAll(".gantt-tick").Count > fitted, "a closer view should mark time more finely");
    }
}
