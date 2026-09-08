using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Components;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The Working-time page against a real seeded SQLite database: the rule form,
/// the live preview, the holiday table and the absence list all render from
/// the stored settings and react to a changed state without a save.
/// </summary>
public sealed class WorkingTimePageTests : BunitContext
{
    private BrowserDatabase Arrange(TempDatabaseFiles files)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("working-time.db", new FakeStorage());

        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkPlanStudio.Services.Auth.WorkspaceRole.Planner);
        Services.AddSingleton(new WorkCenterService(database));
        Services.AddSingleton(new PlantSettingsService(database));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkingTimePage>>(NullLogger<WorkPlanStudio.Pages.WorkingTimePage>.Instance);
        return database;
    }

    [Fact]
    public async Task Renders_rules_preview_holidays_and_absences_from_the_seed()
    {
        using var files = new TempDatabaseFiles();
        Assert.True((await Arrange(files).EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkingTimePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".week-strip")));

        // every rule of the catalogue is explained
        foreach (var rule in WorkingTimeRules.Catalog)
            Assert.Contains($"Rule_{rule.Id}_Text", cut.Markup);

        // NW holidays for the year, Fronleichnam included
        Assert.Contains("Holiday_CorpusChristi", cut.Markup);
        Assert.Contains("Holiday_AllSaints", cut.Markup);
        Assert.DoesNotContain("Holiday_WomensDay", cut.Markup);

        // seven weekday strips, and the seeded absence
        Assert.Equal(7, cut.FindAll(".week-row").Count);
        Assert.Contains("Spindle service", cut.Markup);
        Assert.Contains("Applied_Breaks", cut.Markup);
    }

    [Fact]
    public async Task Changing_the_state_recomputes_the_holidays_before_saving()
    {
        using var files = new TempDatabaseFiles();
        Assert.True((await Arrange(files).EnsureReadyAsync()).IsReady);
        var cut = Render<WorkPlanStudio.Pages.WorkingTimePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wt-state")));

        cut.Find("#wt-state").Change("BE");

        cut.WaitForAssertion(() => Assert.Contains("Holiday_WomensDay", cut.Markup));
        Assert.DoesNotContain("Holiday_CorpusChristi", cut.Markup);
    }

    [Fact]
    public async Task Allowing_sunday_work_on_a_pattern_without_sundays_changes_nothing_but_the_flag()
    {
        using var files = new TempDatabaseFiles();
        Assert.True((await Arrange(files).EnsureReadyAsync()).IsReady);
        var cut = Render<WorkPlanStudio.Pages.WorkingTimePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wt-sunday")));

        cut.Find("#wt-sunday").Change(true);

        cut.WaitForAssertion(() => Assert.DoesNotContain("WorkingTime_FreeSundaysViolation", cut.Markup));
        Assert.DoesNotContain(cut.FindAll(".week-seg"), seg => seg.ClassList.Contains("seg-sunday"));
    }

    [Fact]
    public async Task Saving_persists_the_rules()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var cut = Render<WorkPlanStudio.Pages.WorkingTimePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wt-state")));

        cut.Find("#wt-state").Change("SN");
        await cut.Find("button.btn-primary").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("WorkingTime_Saved", cut.Markup));
        Assert.Equal("SN", (await new PlantSettingsService(database).GetAsync(Xunit.TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public void The_week_strip_draws_a_continuous_pattern_as_seven_full_days()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        var timeline = WorkingTimelineBuilder.Build(ShiftPatterns.Continuous, WorkingTimeRules.Statutory, [], new DateTime(2026, 1, 5), new DateTime(2026, 1, 12));

        var cut = Render<WeekStrip>(parameters => parameters.Add(c => c.Timeline, timeline));

        Assert.Equal(7, cut.FindAll(".week-seg.seg-working").Count);
        Assert.All(cut.FindAll(".week-seg"), seg => Assert.Contains("width:100%", seg.GetAttribute("style")));
    }
}
