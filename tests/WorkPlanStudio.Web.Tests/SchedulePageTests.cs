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
public class SchedulePageTests : BunitContext
{
    private readonly FakeAssistantConfig _assistantConfig = new();

    private FakeScheduleService Arrange(ScheduleResult result)
    {
        var fake = new FakeScheduleService { Result = result };
        Services.AddSingleton<IProductionScheduleService>(fake);
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());

        // Assistant dependencies: the real rule-based narrator (it is the offline
        // default and the demo/test "mock"), a fake config and an unused HttpClient.
        Services.AddSingleton<RuleBasedNarrator>();
        Services.AddSingleton<IAssistantConfig>(_assistantConfig);
        Services.AddSingleton(new HttpClient());
        Services.AddSingleton<ScheduleAssistant>();
        return fake;
    }

    [Fact]
    public void Renders_kpis_gantt_and_job_table_from_the_service_result()
    {
        Arrange(Sample.OnTime());

        var cut = Render<SchedulePage>();

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

        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".empty-state")));
        Assert.Empty(cut.FindAll(".gantt"));
        Assert.Empty(cut.FindAll(".stat-card"));
    }

    [Fact]
    public void Late_jobs_render_late_pills_and_late_bars()
    {
        Arrange(Sample.WithLateJob());

        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".pill.late")));
        Assert.NotEmpty(cut.FindAll(".gantt-bar.late"));
        Assert.Single(cut.FindAll(".gantt-legend"));   // the "late" legend only appears when something is late
    }

    [Fact]
    public void Generate_invokes_the_service_with_the_selected_parameters()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.True(fake.Calls >= 1));   // runs once on load
        var callsAfterLoad = fake.Calls;

        cut.Find(".btn-primary").Click();

        Assert.True(fake.Calls > callsAfterLoad);
        Assert.NotNull(fake.LastParameters);
        Assert.Equal(DispatchRule.EarliestDueDate, fake.LastParameters!.DispatchRule);   // the form default
        Assert.Equal(DueDateRule.Explicit, fake.LastParameters.DueDateRule);
    }

    [Fact]
    public void Choosing_the_NOP_due_rule_swaps_in_its_allowance_field()
    {
        Arrange(Sample.OnTime());
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".param-grid")));

        // selects are, in order: dispatch rule, then target-date rule
        cut.FindAll("select")[1].Change(DueDateRule.NumberOfOperations.ToString());

        // the pass-through localizer echoes keys, so the NOP field's label key is now present
        Assert.Contains("Sched_NopMinutes", cut.Markup);
        Assert.DoesNotContain("Sched_TwkFactor", cut.Markup);
    }

    [Fact]
    public void Explicit_due_dates_are_offered_now_that_orders_carry_one()
    {
        Arrange(Sample.OnTime());

        var cut = Render<SchedulePage>();

        // Production orders supply a real customer due date, so the rule that
        // consumes one is no longer hidden - it is the default.
        cut.WaitForAssertion(() => Assert.Contains("Sched_Due_Explicit", cut.Markup));
    }

    [Fact]
    public void Rejected_order_diagnostics_name_the_order_and_link_to_the_orders_page()
    {
        var result = Sample.OnTime() with
        {
            PreparationErrors =
            [
                new SchedulePreparationIssue(
                    42,
                    "PO-42",
                    20,
                    SchedulePreparationErrorCode.InactiveWorkCenter,
                    "WC-2")
            ]
        };
        Arrange(result);

        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Contains("Sched_RejectedTitle", cut.Markup));
        Assert.Equal("production-orders", cut.Find(".form-banner a").GetAttribute("href"));
        Assert.Contains("PO-42", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Sched_Error_InactiveWorkCenter", cut.Markup);
    }

    [Fact]
    public void Unexpected_scheduler_failure_unlocks_the_generate_button_and_shows_safe_error()
    {
        var fake = Arrange(Sample.OnTime());
        fake.ExceptionToThrow = new InvalidOperationException("internal details");

        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Contains("Error_ScheduleFailed", cut.Markup));
        var generate = cut.Find(".page-head .btn-primary");
        Assert.False(generate.HasAttribute("disabled"));
        Assert.DoesNotContain("internal details", cut.Markup);
    }

    [Fact]
    public void Renders_the_assistant_panel_with_a_rule_based_explanation()
    {
        Arrange(Sample.WithLateJob());

        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".assistant-card")));
        Assert.NotEmpty(cut.FindAll(".assistant-line"));
        Assert.Contains("Sched_Ai_SourceRuleBased", cut.Markup);   // the source badge
        Assert.Contains("Sched_Ai_RecSwitch", cut.Markup);          // the recommendation line
    }

    [Fact]
    public void The_enhance_with_ai_button_is_hidden_until_a_provider_is_configured()
    {
        Arrange(Sample.OnTime());

        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".assistant-card")));
        Assert.DoesNotContain("Sched_Ai_AskAi", cut.Markup);
    }

    [Fact]
    public void The_enhance_with_ai_button_appears_once_a_provider_is_configured()
    {
        _assistantConfig.Settings = new AssistantSettings
        {
            Enabled = true,
            Endpoint = "https://example/v1",
            Model = "m",
            ApiKey = "secret"
        };
        Arrange(Sample.OnTime());

        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Contains("Sched_Ai_AskAi", cut.Markup));
    }
}
