using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// End-to-end user flows for the Scheduling page, driven through a real browser.
/// These are the "test it through the program" checks: they prove the parameters,
/// the engine, the mapping and the rendering work together — and that a change to
/// the parameters is visibly reflected in the schedule.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public sealed class ScheduleE2ETests
{
    private readonly PlaywrightFixture _fixture;

    public ScheduleE2ETests(PlaywrightFixture fixture) => _fixture = fixture;

    private async Task<(IBrowserContext Context, SchedulePage Page)> OpenAsync()
    {
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1320, Height = 980 }
        });
        var schedule = new SchedulePage(await context.NewPageAsync(), _fixture.BaseUrl);
        await schedule.GotoAsync();
        return (context, schedule);
    }

    [Fact]
    public async Task Default_schedule_renders_kpis_gantt_and_jobs()
    {
        var (context, schedule) = await OpenAsync();
        await using var _ = context;

        Assert.Equal(4, await schedule.KpiCards.CountAsync());
        Assert.True(await schedule.GanttBars.CountAsync() > 0, "expected Gantt bars");
        Assert.True(await schedule.JobRows.CountAsync() > 0, "expected job rows");
    }

    [Fact]
    public async Task Tightening_the_targets_makes_more_jobs_late_and_marks_them_red()
    {
        var (context, schedule) = await OpenAsync();
        await using var _ = context;

        // Loose targets (flow factor 3.0) → a healthy, all-on-time schedule.
        await schedule.SetFlowFactorAsync("3");
        await schedule.GenerateAsync();
        int lateLoose = await schedule.LatePills.CountAsync();
        await schedule.ScreenshotAsync("schedule-ontime.png");

        // Tight targets (flow factor 0.5) → the same jobs can no longer hit them.
        await schedule.SetFlowFactorAsync("0.5");
        await schedule.GenerateAsync();
        await schedule.LatePills.First.WaitForAsync(new() { Timeout = 15_000 });
        int lateTight = await schedule.LatePills.CountAsync();
        await schedule.ScreenshotAsync("schedule-late.png");

        // The same parameter, two values, a visibly different schedule.
        Assert.True(lateTight > lateLoose, $"tightening should make more jobs late ({lateLoose} → {lateTight})");
        Assert.True(await schedule.LateBars.CountAsync() > 0, "expected late (red-ringed) bars");
    }

    [Fact]
    public async Task Changing_the_dispatch_rule_reschedules_the_same_operations()
    {
        var (context, schedule) = await OpenAsync();
        await using var _ = context;

        int barsBefore = await schedule.GanttBars.CountAsync();
        var makespanBefore = await schedule.MakespanTextAsync();   // EDD (the form default)

        await schedule.SetDispatchRuleAsync("LongestProcessingTime");
        await schedule.GenerateAsync();

        // every operation is still scheduled (a different sequence, but all of them) …
        Assert.Equal(barsBefore, await schedule.GanttBars.CountAsync());
        // … and the rule visibly changes the result (different makespan).
        Assert.NotEqual(makespanBefore, await schedule.MakespanTextAsync());
    }

    [Fact]
    public async Task The_same_seed_reproduces_the_same_makespan()
    {
        var (context, schedule) = await OpenAsync();
        await using var _ = context;

        await schedule.SetSeedAsync("4242");
        await schedule.GenerateAsync();
        var first = await schedule.MakespanTextAsync();

        await schedule.GenerateAsync();
        var second = await schedule.MakespanTextAsync();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task The_page_can_be_switched_to_german()
    {
        var (context, schedule) = await OpenAsync();
        await using var _ = context;

        await schedule.SwitchToGermanAsync();

        Assert.Contains("Produktionsplanung", await schedule.Heading.InnerTextAsync());
    }
}
