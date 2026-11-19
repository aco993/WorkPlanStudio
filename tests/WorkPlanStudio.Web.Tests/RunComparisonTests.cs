using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Scheduling;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.Services.Scheduling;
using SchedulePage = WorkPlanStudio.Pages.Schedule;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// A planner changes a parameter to see what the change does. These hold the
/// comparison to the truth: better and worse only where a direction is better,
/// the changed parameters only, and "nothing changed" only when nothing did.
/// </summary>
public sealed class RunComparisonTests : AppBunitContext
{
    private static ScheduleKpis Kpis(long makespan = 100, int late = 0, long tardiness = 0, double util = 0.3, int jobs = 7) =>
        new(makespan, (jobs - late) / (double)jobs, tardiness, util, late, jobs);

    // ----- the model -----

    [Fact]
    public void A_shorter_makespan_and_fewer_late_jobs_are_better_and_longer_and_more_are_worse()
    {
        var changes = RunComparison.Kpis(Kpis(makespan: 100, late: 2, tardiness: 50), Kpis(makespan: 80, late: 0, tardiness: 0));
        Assert.All(changes.Where(c => c.Kpi != "Utilization"), c => Assert.Equal(Verdict.Better, c.Verdict));

        var back = RunComparison.Kpis(Kpis(makespan: 80, late: 0, tardiness: 0), Kpis(makespan: 100, late: 2, tardiness: 50));
        Assert.All(back.Where(c => c.Kpi != "Utilization"), c => Assert.Equal(Verdict.Worse, c.Verdict));
    }

    [Fact]
    public void Utilisation_moving_is_reported_but_never_judged()
    {
        var up = RunComparison.Kpis(Kpis(util: 0.3), Kpis(util: 0.5)).Single(c => c.Kpi == "Utilization");
        var down = RunComparison.Kpis(Kpis(util: 0.5), Kpis(util: 0.3)).Single(c => c.Kpi == "Utilization");
        Assert.Equal(Verdict.Neutral, up.Verdict);
        Assert.Equal(Verdict.Neutral, down.Verdict);
    }

    [Fact]
    public void Only_the_parameters_that_differ_are_named()
    {
        var before = new SchedulingParameters();
        var after = before with { DispatchRule = DispatchRule.ShortestProcessingTime, Seed = before.Seed + 1 };

        Assert.Equal(["DispatchRule", "Seed"], RunComparison.ChangedParameters(before, after));
        Assert.Empty(RunComparison.ChangedParameters(before, before with { }));
    }

    [Fact]
    public void Same_parameters_and_different_figures_is_a_data_change_not_nothing()
    {
        var p = new SchedulingParameters();
        Assert.Equal(RunDifference.Identical, RunComparison.Difference(p, Kpis(), p with { }, Kpis()));
        Assert.Equal(RunDifference.DataChanged, RunComparison.Difference(p, Kpis(makespan: 100), p with { }, Kpis(makespan: 120)));
        Assert.Equal(RunDifference.ParametersChanged,
            RunComparison.Difference(p, Kpis(), p with { TwkFlowFactor = 3.0 }, Kpis(makespan: 90)));
        Assert.Equal(RunDifference.ParametersChangedNoEffect,
            RunComparison.Difference(p, Kpis(), p with { TwkFlowFactor = 3.0 }, Kpis()));
    }

    // ----- the page -----

    private readonly TempDatabaseFiles _files = new();

    private FakeScheduleService Arrange(ScheduleResult result)
    {
        var fake = new FakeScheduleService { Result = result };
        JSInterop.Mode = JSRuntimeMode.Loose;
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
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("comparison.db", new FakeStorage())));
        return fake;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private static async Task GenerateAsync(IRenderedComponent<SchedulePage> cut, FakeScheduleService fake)
    {
        var calls = fake.Calls;
        await cut.InvokeAsync(() => cut.Find(".page-head .btn-primary").Click());
        cut.WaitForAssertion(() => Assert.True(fake.Calls > calls));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".page-head .btn-primary[disabled]")));
    }

    [Fact]
    public void The_first_run_has_nothing_to_be_compared_with()
    {
        Arrange(Sample.OnTime());
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".stat-grid")));

        Assert.Empty(cut.FindAll(".run-comparison"));
    }

    [Fact]
    public async Task Running_again_unchanged_says_the_search_is_deterministic()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".stat-grid")));

        await GenerateAsync(cut, fake);

        cut.WaitForAssertion(() => Assert.Contains("Cmp_Why_Identical", cut.Find(".run-comparison").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".run-comparison-changed"));
    }

    [Fact]
    public async Task A_changed_rule_is_named_and_the_figures_say_which_way_they_moved()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".stat-grid")));

        await cut.InvokeAsync(() => cut.Find("#sched-rule").Change(nameof(DispatchRule.ShortestProcessingTime)));
        fake.Result = Sample.WithLateJob();
        await GenerateAsync(cut, fake);

        cut.WaitForAssertion(() => Assert.Contains("Cmp_Why_ParametersChanged", cut.Find(".run-comparison").TextContent, StringComparison.Ordinal));
        Assert.Contains("Sched_DispatchRule", cut.Find(".run-comparison-changed").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll(".cmp-kpi.cmp-worse"));
    }

    [Fact]
    public async Task Same_parameters_with_different_figures_blames_the_data()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".stat-grid")));

        fake.Result = Sample.WithLateJob();
        await GenerateAsync(cut, fake);

        cut.WaitForAssertion(() => Assert.Contains("Cmp_Why_DataChanged", cut.Find(".run-comparison").TextContent, StringComparison.Ordinal));
    }
}
