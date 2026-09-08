using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Chat;
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

        // The conversation over the run: on-device answerer + façade, and the
        // plant settings it reads the rules from (a real temp database; the
        // samples without a horizon never touch it).
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<OfflineScheduleAnswerer>();
        Services.AddSingleton<ScheduleChat>();
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("schedule-page.db", new FakeStorage())));
        return fake;
    }

    private readonly TempDatabaseFiles _files = new();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    // ----- chat -----

    [Fact]
    public void The_chat_offers_suggestions_and_answers_a_clicked_one_on_device()
    {
        Arrange(Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) });
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".chat-suggestions .chip")));
        Assert.Empty(cut.FindAll(".chat-turn"));

        cut.Find(".chat-suggestions .chip").Click();   // "Which work center is the bottleneck?"

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".chat-turn").Count));
        Assert.Contains("Chat_Suggest_Bottleneck", cut.Find(".chat-turn.user .chat-bubble").TextContent);
        Assert.StartsWith("Chat_Bottleneck", cut.Find(".chat-turn.assistant .chat-bubble").TextContent);
        Assert.Equal("Chat_SourceOffline", cut.Find(".chat-turn.assistant .chat-source").TextContent);
        Assert.NotEmpty(cut.FindAll(".chat-input .btn-ghost"));   // "clear" appears once there is a thread
    }

    [Fact]
    public void A_typed_question_is_submitted_and_the_box_is_cleared()
    {
        Arrange(Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) });
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#chat-question")));

        cut.Find("#chat-question").Input("how is WP-2 doing?");
        cut.Find(".chat-input").Submit();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".chat-turn").Count));
        Assert.StartsWith("Chat_Order", cut.Find(".chat-turn.assistant .chat-bubble").TextContent);
        Assert.Contains("CNC-200", cut.Find(".chat-turn.assistant .chat-bubble").TextContent);   // WP-2's only step
        Assert.Equal("", cut.Find("#chat-question").GetAttribute("value") ?? "");
    }

    [Fact]
    public void Clearing_the_conversation_removes_the_thread_and_regenerating_starts_fresh()
    {
        var fake = Arrange(Sample.OnTime() with { Horizon = new DateTime(2026, 6, 1, 6, 0, 0) });
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".chat-suggestions .chip")));
        cut.Find(".chat-suggestions .chip").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".chat-turn").Count));

        cut.Find(".chat-input .btn-ghost").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".chat-turn")));

        cut.Find(".chat-suggestions .chip").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".chat-turn").Count));
        var callsBefore = fake.Calls;
        cut.Find(".page-head .btn-primary").Click();    // a new run resets the conversation
        cut.WaitForAssertion(() => Assert.True(fake.Calls > callsBefore));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".chat-turn")));
    }

    [Fact]
    public void Without_a_schedule_the_chat_says_there_is_nothing_to_talk_about()
    {
        Arrange(ScheduleResult.Empty(480));
        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".empty-state")));
        Assert.Empty(cut.FindAll(".chat"));   // the assistant card only exists with data
    }

    [Fact]
    public void The_settings_dialog_switches_endpoint_and_model_with_the_provider()
    {
        Arrange(Sample.OnTime());
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".assistant-card .icon-btn")));

        cut.Find(".assistant-card .icon-btn").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card select")));
        cut.Find(".modal-card select").Change("Anthropic");

        var inputs = cut.FindAll(".modal-card input.input");
        Assert.Equal(AssistantSettings.DefaultEndpoint(AssistantProvider.Anthropic), inputs[0].GetAttribute("value"));
        Assert.Equal(AssistantSettings.DefaultModel(AssistantProvider.Anthropic), inputs[1].GetAttribute("value"));

        // A hand-typed model survives the next provider switch.
        cut.Find(".modal-card input.input:nth-of-type(1)");
        inputs[1].Input("my-model");
        cut.Find(".modal-card select").Change("Gemini");
        inputs = cut.FindAll(".modal-card input.input");
        Assert.Equal(AssistantSettings.DefaultEndpoint(AssistantProvider.Gemini), inputs[0].GetAttribute("value"));
        Assert.Equal("my-model", inputs[1].GetAttribute("value"));
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
    public void Closed_time_is_shaded_and_the_axis_shows_real_dates_when_a_horizon_is_known()
    {
        var horizon = new DateTime(2026, 6, 1, 6, 0, 0);
        var onTime = Sample.OnTime();
        var result = onTime with
        {
            Horizon = horizon,
            MakespanSeconds = 40 * 3600,
            TotalPausedSeconds = 1800,
            Rows =
            [
                new GanttRow("SAW-10 — Cut-off Saw",
                    [new GanttBar(1, "WP-1", 0, 1, 0, 6 * 3600, IsLate: false) { PausedSeconds = 1800 }],
                    [
                        new GanttClosedSegment(4 * 3600, 4 * 3600 + 1800, WorkingTime.SegmentKind.Break, "day"),
                        new GanttClosedSegment(18 * 3600, 42 * 3600, WorkingTime.SegmentKind.Holiday, "CorpusChristi")
                    ])
            ]
        };
        Arrange(result);

        var cut = Render<SchedulePage>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".gantt-closed").Count));
        Assert.Single(cut.FindAll(".gantt-closed.closed-holiday"));
        Assert.Contains("Holiday_CorpusChristi", cut.Find(".gantt-closed.closed-holiday").GetAttribute("title"));
        Assert.Single(cut.FindAll(".gantt-bar.paused"));
        Assert.Contains("Sched_ClosedLegend", cut.Markup);
        Assert.Contains("Sched_TotalPaused", cut.Markup);

        // axis ticks fall on midnight and are labelled with the date
        var ticks = cut.FindAll(".gantt-tick");
        Assert.NotEmpty(ticks);
        Assert.Contains(ticks, t => t.TextContent.Contains("2.6.", StringComparison.Ordinal));
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
