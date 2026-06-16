using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using SchedulePage = WorkPlanStudio.Pages.Schedule;   // disambiguate from Scheduling.Schedule

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Component tests for the Scheduling page, rendered in-memory with bUnit against
/// a fake service — no browser, no database. They verify the page's rendering and
/// interaction logic (the engine is tested separately).
/// </summary>
public class SchedulePageTests : Bunit.TestContext
{
    private FakeScheduleService Arrange(ScheduleResult result)
    {
        var fake = new FakeScheduleService { Result = result };
        Services.AddSingleton<IProductionScheduleService>(fake);
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        return fake;
    }

    [Fact]
    public void Renders_kpis_gantt_and_job_table_from_the_service_result()
    {
        Arrange(Sample.OnTime());

        var cut = RenderComponent<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".stat-card").Count));
        Assert.Equal(2, cut.FindAll(".gantt-row").Count);
        Assert.Equal(2, cut.FindAll(".gantt-bar").Count);
        Assert.Equal(2, cut.FindAll(".data-table tbody tr").Count);
        Assert.Empty(cut.FindAll(".empty-state"));
        Assert.Empty(cut.FindAll(".pill.late"));
    }

    [Fact]
    public void Shows_the_empty_state_when_there_is_nothing_to_schedule()
    {
        Arrange(ScheduleResult.Empty(480));

        var cut = RenderComponent<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".empty-state")));
        Assert.Empty(cut.FindAll(".gantt"));
        Assert.Empty(cut.FindAll(".stat-card"));
    }

    [Fact]
    public void Late_jobs_render_late_pills_and_late_bars()
    {
        Arrange(Sample.WithLateJob());

        var cut = RenderComponent<SchedulePage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pill.late")));
        Assert.NotEmpty(cut.FindAll(".gantt-bar.late"));
        Assert.Single(cut.FindAll(".gantt-legend"));   // the "late" legend only appears when something is late
    }

    [Fact]
    public void Generate_invokes_the_service_with_the_selected_parameters()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = RenderComponent<SchedulePage>();
        cut.WaitForAssertion(() => Assert.True(fake.Calls >= 1));   // runs once on load
        var callsAfterLoad = fake.Calls;

        cut.Find(".btn-primary").Click();

        Assert.True(fake.Calls > callsAfterLoad);
        Assert.NotNull(fake.LastParameters);
        Assert.Equal(DispatchRule.EarliestDueDate, fake.LastParameters!.DispatchRule);   // the form default
        Assert.Equal(DueDateRule.TotalWorkContent, fake.LastParameters.DueDateRule);
    }

    [Fact]
    public void Choosing_the_NOP_due_rule_swaps_in_its_allowance_field()
    {
        Arrange(Sample.OnTime());
        var cut = RenderComponent<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".param-grid")));

        // selects are, in order: dispatch rule, then target-date rule
        cut.FindAll("select")[1].Change(DueDateRule.NumberOfOperations.ToString());

        // the pass-through localizer echoes keys, so the NOP field's label key is now present
        Assert.Contains("Sched_NopMinutes", cut.Markup);
        Assert.DoesNotContain("Sched_TwkFactor", cut.Markup);
    }
}
