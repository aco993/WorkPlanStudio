using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.WorkingTime;
using SchedulePage = WorkPlanStudio.Pages.Schedule;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The Gantt chart is the feature the project is built around, and it used to be
/// operable with a mouse and with nothing else: the bars were <c>div</c>s whose only
/// carrier of the operation number, the timestamps and the reason for a gap was a
/// <c>title</c> attribute, and the chart's own legend said "hover for the reason".
/// These tests pin the three things that make it usable without a pointer — a name
/// on every mark, one tab stop with arrow keys inside it, and the same information
/// available as text.
/// </summary>
public sealed class GanttAccessibilityTests : AppBunitContext
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
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("gantt-a11y.db", new FakeStorage())));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    /// <summary>A run with two lanes, two bars and one closed stretch with a reason.</summary>
    private static ScheduleResult WithClosedTime() => Sample.OnTime() with
    {
        Horizon = new DateTime(2026, 6, 1, 6, 0, 0),
        MakespanSeconds = 40 * 3600,
        Rows =
        [
            new GanttRow("SAW-10 — Cut-off Saw",
                [new GanttBar(1, "WP-1", 0, 10, 0, 6 * 3600, IsLate: false)],
                [new GanttClosedSegment(18 * 3600, 42 * 3600, SegmentKind.Holiday, "CorpusChristi")]),
            new GanttRow("CNC-200 — Turning",
                [new GanttBar(2, "WP-2", 1, 20, 6 * 3600, 12 * 3600, IsLate: false)],
                [])
        ]
    };

    private IReadOnlyList<AngleSharp.Dom.IElement> Cells(IRenderedComponent<SchedulePage> cut) =>
        cut.FindAll(".gantt-bar, .gantt-closed");

    [Fact]
    public void Every_bar_and_closed_stretch_is_a_button_with_an_accessible_name()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.Equal(3, Cells(cut).Count));

        foreach (var cell in Cells(cut))
        {
            Assert.Equal("BUTTON", cell.TagName);
            var name = cell.GetAttribute("aria-label");
            Assert.False(string.IsNullOrWhiteSpace(name), "a chart mark with no name announces as 'button'");
        }
    }

    /// <summary>
    /// The name has to say which machine: the lane label is a sibling element, so a
    /// reader arriving on a bar by keyboard has nothing else to tell them where they are.
    /// </summary>
    [Fact]
    public void A_bar_names_its_work_center_its_job_its_operation_and_its_timestamps()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".gantt-bar")));

        var name = cut.Find(".gantt-bar").GetAttribute("aria-label")!;
        Assert.Contains("SAW-10", name, StringComparison.Ordinal);
        Assert.Contains("WP-1", name, StringComparison.Ordinal);
        Assert.Contains("10", name, StringComparison.Ordinal);                  // the operation number
        Assert.Contains(Format.Stamp(new DateTime(2026, 6, 1, 6, 0, 0)), name, StringComparison.Ordinal);
        Assert.Contains(Format.Stamp(new DateTime(2026, 6, 1, 12, 0, 0)), name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_stretch_names_the_reason_it_is_closed()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".gantt-closed")));

        var name = cut.Find(".gantt-closed").GetAttribute("aria-label")!;
        Assert.Contains("Segment_Holiday", name, StringComparison.Ordinal);
        Assert.Contains("Holiday_CorpusChristi", name, StringComparison.Ordinal);
        Assert.Contains("SAW-10", name, StringComparison.Ordinal);
    }

    /// <summary>
    /// A chart with fifty marks must not be fifty tab stops. Exactly one carries
    /// tabindex 0; the arrow keys move it.
    /// </summary>
    [Fact]
    public async Task The_chart_is_one_tab_stop_and_the_arrow_keys_move_it()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.Equal(3, Cells(cut).Count));
        Assert.Single(Cells(cut), c => c.GetAttribute("tabindex") == "0");

        // Lane one runs bar (0 h) then holiday (18 h), ordered by start time.
        Assert.Contains("gantt-bar", Cells(cut).Single(c => c.GetAttribute("tabindex") == "0").ClassList);

        await PressAsync(cut, "ArrowRight");

        cut.WaitForAssertion(() => Assert.Single(Cells(cut), c => c.GetAttribute("tabindex") == "0"));
        Assert.Contains("gantt-closed", Cells(cut).Single(c => c.GetAttribute("tabindex") == "0").ClassList);
    }

    [Fact]
    public async Task Arrow_down_moves_to_the_next_work_center_and_arrow_up_comes_back()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.Equal(3, Cells(cut).Count));

        await PressAsync(cut, "ArrowDown");
        cut.WaitForAssertion(() => Assert.Equal("gantt-bar-1-0", Focused(cut).Id));

        await PressAsync(cut, "ArrowUp");
        cut.WaitForAssertion(() => Assert.Equal("gantt-bar-0-0", Focused(cut).Id));
    }

    [Fact]
    public async Task Moving_the_roving_focus_asks_the_browser_to_follow_it()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.Equal(3, Cells(cut).Count));

        await PressAsync(cut, "ArrowRight");

        cut.WaitForAssertion(() => Assert.Contains(
            JSInterop.Invocations,
            invocation => invocation.Identifier == "workplanFocus.byId"
                          && invocation.Arguments.Count > 0
                          && (invocation.Arguments[0] as string) == "gantt-gap-0-0"));
    }

    [Fact]
    public async Task End_and_Home_jump_to_the_ends_of_the_lane()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.Equal(3, Cells(cut).Count));

        await PressAsync(cut, "End");
        cut.WaitForAssertion(() => Assert.Equal("gantt-gap-0-0", Focused(cut).Id));

        await PressAsync(cut, "Home");
        cut.WaitForAssertion(() => Assert.Equal("gantt-bar-0-0", Focused(cut).Id));
    }

    /// <summary>
    /// The explanation region is rendered from the first paint, empty. A live region
    /// inserted together with its content is not announced by any screen reader —
    /// the same defect the chat thread had.
    /// </summary>
    [Fact]
    public async Task Activating_a_mark_writes_its_explanation_into_a_live_region_that_already_existed()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".gantt-detail")));

        var live = cut.Find(".gantt-detail");
        Assert.Equal("polite", live.GetAttribute("aria-live"));
        Assert.Equal("status", live.GetAttribute("role"));
        Assert.Equal("", live.TextContent.Trim());

        await ActAsync(cut, ".gantt-closed", element => element.Click());

        cut.WaitForAssertion(() => Assert.Contains("Holiday_CorpusChristi", cut.Find(".gantt-detail").TextContent, StringComparison.Ordinal));
        Assert.Equal("true", cut.Find(".gantt-closed").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task Escape_clears_the_explanation()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".gantt-closed")));
        await ActAsync(cut, ".gantt-closed", element => element.Click());
        cut.WaitForAssertion(() => Assert.NotEqual("", cut.Find(".gantt-detail").TextContent.Trim()));

        await ActAsync(cut, ".gantt-closed", element => element.KeyDown("Escape"));

        cut.WaitForAssertion(() => Assert.Equal("", cut.Find(".gantt-detail").TextContent.Trim()));
        Assert.Equal("false", cut.Find(".gantt-closed").GetAttribute("aria-pressed"));
    }

    /// <summary>
    /// The picture is not the only copy of the data: one table row per mark, in the
    /// DOM whether or not the "show as table" toggle has been pressed.
    /// </summary>
    [Fact]
    public void The_chart_has_a_text_equivalent_with_a_row_for_every_mark()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".chart-table")));

        var rows = cut.FindAll(".chart-table tbody tr");
        Assert.Equal(3, rows.Count);                                   // two bars and one closed stretch
        Assert.Contains(rows, r => r.TextContent.Contains("Holiday_CorpusChristi", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.TextContent.Contains("WP-2", StringComparison.Ordinal));
        Assert.All(rows, r => Assert.Equal("row", r.QuerySelector("th")!.GetAttribute("scope")));
    }

    [Fact]
    public async Task The_text_equivalent_can_be_shown_on_screen_and_says_so()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".gantt-table-toggle")));
        Assert.Equal("false", cut.Find(".gantt-table-toggle").GetAttribute("aria-expanded"));
        Assert.Contains("sr-only", cut.Find("#gantt-table").ClassList);

        await ActAsync(cut, ".gantt-table-toggle", element => element.Click());

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".gantt-table-toggle").GetAttribute("aria-expanded")));
        Assert.DoesNotContain("sr-only", cut.Find("#gantt-table").ClassList);
    }

    /// <summary>
    /// The defect this whole class exists for, stated directly: no fact about the
    /// chart may live in a <c>title</c> and nowhere else.
    /// </summary>
    [Fact]
    public void No_chart_mark_carries_its_information_only_in_a_title_attribute()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.Equal(3, Cells(cut).Count));

        foreach (var cell in Cells(cut))
        {
            var title = cell.GetAttribute("title");
            if (string.IsNullOrEmpty(title))
                continue;

            var name = cell.GetAttribute("aria-label") ?? "";
            Assert.Contains(title, name, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Eight fixed hues were the only thing separating two jobs, in both themes. The
    /// letter is the second channel.
    /// </summary>
    [Fact]
    public void The_job_colour_dot_carries_a_letter_as_well_as_a_colour()
    {
        Arrange(WithClosedTime());

        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".jobs-table .color-dot")));

        var dots = cut.FindAll(".jobs-table .color-dot");
        Assert.Equal(["A", "B"], dots.Select(d => d.TextContent).ToArray());
        Assert.All(dots, d => Assert.Equal("true", d.GetAttribute("aria-hidden")));
    }

    private static AngleSharp.Dom.IElement Focused(IRenderedComponent<SchedulePage> cut) =>
        cut.FindAll(".gantt-bar, .gantt-closed").Single(c => c.GetAttribute("tabindex") == "0");

    /// <summary>
    /// Finds the mark that currently holds the roving tabindex and presses a key on
    /// it, both inside the renderer's own dispatch loop.
    /// <para>
    /// Doing it in two statements is a race the suite actually lost, about once in
    /// four full runs and never in isolation: the page finishes its asynchronous
    /// work between the find and the key press, the tree re-renders, and the
    /// handler id the found element carried no longer exists —
    /// <c>UnknownEventHandlerIdException</c>. It reads as flakiness and is not; it
    /// is a stale element. <c>InvokeAsync</c> closes the window.
    /// </para>
    /// </summary>
    private static Task PressAsync(IRenderedComponent<SchedulePage> cut, string key) =>
        cut.InvokeAsync(() => Focused(cut).KeyDown(key));

    /// <summary>The same guard for a mark located by selector rather than by focus.</summary>
    private static Task ActAsync(IRenderedComponent<SchedulePage> cut, string selector, Action<AngleSharp.Dom.IElement> act) =>
        cut.InvokeAsync(() => act(cut.Find(selector)));
}
