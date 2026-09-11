using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Chat;
using SchedulePage = WorkPlanStudio.Pages.Schedule;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// What a person can do to a scheduling run while it is happening, and what the
/// page refuses to start in the first place. The run itself is a fake here; these
/// tests are about the controls around it.
/// <para>
/// Every interaction goes through <see cref="ActAsync"/>. Finding an element and
/// raising an event on it in two statements is a race in bUnit: a re-render between
/// the two invalidates the handler id the found element carries, and the failure
/// looks like flakiness rather than like the stale element it is.
/// </para>
/// </summary>
public sealed class ScheduleRunControlTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    private FakeScheduleService Arrange(ScheduleResult result)
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
        Services.AddSingleton(new PlantSettingsService(_files.CreateDatabase("run-control.db", new FakeStorage())));
        return fake;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private static Task ActAsync(IRenderedComponent<SchedulePage> cut, string selector, Action<AngleSharp.Dom.IElement> act) =>
        cut.InvokeAsync(() => act(cut.Find(selector)));

    private static IRenderedComponent<SchedulePage> Loaded(ScheduleRunControlTests owner)
    {
        var cut = owner.Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#sched-multistart")));
        return cut;
    }

    // ----- the evaluation cap (the pair of fields the engine refuses together) -----

    [Fact]
    public async Task The_evaluation_cap_blocks_the_run_and_marks_both_fields()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = Loaded(this);
        cut.WaitForAssertion(() => Assert.True(fake.Calls >= 1));
        var callsAfterLoad = fake.Calls;

        // 64 is a legal restart count and 20 000 a legal step budget; together they
        // are 1.28 million candidate schedules, which the engine rejects by throwing.
        await ActAsync(cut, "#sched-multistart", input => input.Change("64"));
        await ActAsync(cut, "#sched-localsearch", input => input.Change("20000"));

        var error = cut.Find("#sched-budget-error");
        Assert.Equal("alert", error.GetAttribute("role"));
        Assert.Contains("Run_BudgetExceeded", error.TextContent, StringComparison.Ordinal);

        // The message belongs to both inputs, because either of them can fix it.
        foreach (var id in new[] { "#sched-multistart", "#sched-localsearch" })
        {
            var input = cut.Find(id);
            Assert.Equal("true", input.GetAttribute("aria-invalid"));
            Assert.Contains("sched-budget-error", input.GetAttribute("aria-describedby")!, StringComparison.Ordinal);
        }

        Assert.True(cut.Find("#sched-generate").HasAttribute("disabled"));

        // And pressing it anyway does not reach the service.
        await ActAsync(cut, "#sched-generate", button => button.Click());
        Assert.Equal(callsAfterLoad, fake.Calls);
    }

    [Fact]
    public async Task Bringing_the_pair_back_inside_the_cap_clears_the_error_and_the_run_proceeds()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = Loaded(this);
        cut.WaitForAssertion(() => Assert.True(fake.Calls >= 1));

        await ActAsync(cut, "#sched-multistart", input => input.Change("64"));
        await ActAsync(cut, "#sched-localsearch", input => input.Change("20000"));
        Assert.NotEmpty(cut.FindAll("#sched-budget-error"));

        await ActAsync(cut, "#sched-localsearch", input => input.Change("3000"));

        Assert.Empty(cut.FindAll("#sched-budget-error"));
        Assert.Equal("false", cut.Find("#sched-multistart").GetAttribute("aria-invalid"));
        Assert.False(cut.Find("#sched-generate").HasAttribute("disabled"));

        var callsBefore = fake.Calls;
        await ActAsync(cut, "#sched-generate", button => button.Click());
        cut.WaitForAssertion(() => Assert.True(fake.Calls > callsBefore));
        Assert.Equal(64, fake.LastParameters!.MultiStartRuns);
        Assert.Equal(3000, fake.LastParameters.LocalSearchMaxSteps);
    }

    // ----- progress -----

    [Fact]
    public void Progress_renders_as_a_progressbar_and_the_busy_state_reaches_the_live_region()
    {
        var fake = Arrange(Sample.OnTime());
        fake.UseGate = true;
        fake.ProgressToReport =
        [
            new ScheduleRunProgress(0, 8, double.PositiveInfinity),
            new ScheduleRunProgress(3, 8, 10_394.85)
        ];

        var cut = Render<SchedulePage>();

        // The run is held open, so this is the page mid-run rather than after it.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=progressbar]")));
        var bar = cut.Find("[role=progressbar]");
        Assert.Equal("38", bar.GetAttribute("aria-valuenow"));      // 3 of 8
        Assert.Equal("0", bar.GetAttribute("aria-valuemin"));
        Assert.Equal("100", bar.GetAttribute("aria-valuemax"));
        Assert.Contains("Run_Progress", bar.GetAttribute("aria-valuetext")!, StringComparison.Ordinal);
        Assert.Equal("run-progress-label", bar.GetAttribute("aria-labelledby"));

        // Announced as busy, and honest about where the work happens.
        Assert.Equal("Run_Started", cut.Find(".sched-results > .sr-only[role=status]").TextContent);
        Assert.Contains("Run_SameThreadNote", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("true", cut.Find(".sched-results").GetAttribute("aria-busy"));

        fake.Gate.SetResult();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=progressbar]")));
        cut.WaitForAssertion(() =>
            Assert.Equal("Ui_ScheduleAnnounced", cut.Find(".sched-results > .sr-only[role=status]").TextContent));
    }

    // ----- cancelling -----

    [Fact]
    public async Task Cancelling_a_run_stops_it_and_leaves_the_previous_schedule_on_screen()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = Loaded(this);

        // One complete run first, so there is something to preserve.
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".jobs-table tbody tr").Count));
        var before = cut.Find(".jobs-table").InnerHtml;

        // The next run hangs, and is cancelled from the page's own button.
        fake.UseGate = true;
        fake.Result = Sample.WithLateJob();
        await ActAsync(cut, "#sched-generate", button => button.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#sched-cancel")));

        await ActAsync(cut, "#sched-cancel", button => button.Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#sched-cancel")));
        Assert.Empty(cut.FindAll("[role=progressbar]"));
        Assert.False(cut.Find("#sched-generate").HasAttribute("disabled"));

        // The schedule from the first run is untouched, and the second run's
        // (different) result never arrived.
        Assert.Equal(before, cut.Find(".jobs-table").InnerHtml);
        Assert.Empty(cut.FindAll(".pill.late"));

        // A cancellation is an outcome, not a failure: status region, no error banner.
        Assert.Equal("Run_Cancelled", cut.Find(".sched-results > .sr-only[role=status]").TextContent);
        Assert.DoesNotContain("Error_ScheduleFailed", cut.Markup, StringComparison.Ordinal);

        // Cancel destroyed the element that had focus, so focus is put back on the
        // control that replaced it rather than left on the document body.
        Assert.Contains(
            JSInterop.Invocations["workplanFocus.byId"],
            invocation => Equals(invocation.Arguments[0], "sched-generate"));
    }

    // ----- the acceptance rule -----

    [Fact]
    public async Task The_improvement_rule_select_reaches_the_engine_parameters()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = Loaded(this);
        cut.WaitForAssertion(() => Assert.True(fake.Calls >= 1));

        // The measured default is what the first run already used.
        Assert.Equal(LocalSearchAcceptance.BestInsertion, fake.LastParameters!.LocalSearchAcceptance);

        var select = cut.Find("#sched-acceptance");
        Assert.Equal(3, select.QuerySelectorAll("option").Length);
        Assert.Equal("sched-acceptance-hint", select.GetAttribute("aria-describedby"));
        Assert.Contains("Run_Acceptance_BestHint", cut.Find("#sched-acceptance-hint").TextContent, StringComparison.Ordinal);

        await ActAsync(cut, "#sched-acceptance", s => s.Change(nameof(LocalSearchAcceptance.SteepestDescent)));

        // The explanation follows the selection; it is a sibling of the select and
        // not a child, so it describes the control without renaming it.
        Assert.Contains("Run_Acceptance_SteepestHint", cut.Find("#sched-acceptance-hint").TextContent, StringComparison.Ordinal);

        var callsBefore = fake.Calls;
        await ActAsync(cut, "#sched-generate", button => button.Click());
        cut.WaitForAssertion(() => Assert.True(fake.Calls > callsBefore));
        Assert.Equal(LocalSearchAcceptance.SteepestDescent, fake.LastParameters.LocalSearchAcceptance);
    }

    [Fact]
    public void The_form_starts_from_the_engines_own_defaults()
    {
        var fake = Arrange(Sample.OnTime());
        var cut = Render<SchedulePage>();
        cut.WaitForAssertion(() => Assert.NotNull(fake.LastParameters));

        var defaults = new SchedulingParameters();
        var used = fake.LastParameters!;

        Assert.Equal(defaults.MultiStartRuns, used.MultiStartRuns);
        Assert.Equal(defaults.LocalSearchMaxSteps, used.LocalSearchMaxSteps);
        Assert.Equal(defaults.LocalSearchAcceptance, used.LocalSearchAcceptance);
        Assert.Equal(defaults.TwkFlowFactor, used.TwkFlowFactor);
        Assert.Equal(defaults.NopSecondsPerOp, used.NopSecondsPerOp);
        Assert.Equal(defaults.SlackSeconds, used.SlackSeconds);
        Assert.Equal(defaults.ConstantAllowanceSeconds, used.ConstantAllowanceSeconds);
        Assert.Equal(defaults.Seed, used.Seed);
        Assert.Equal(defaults.DispatchRule, used.DispatchRule);

        // The display day travels beside the parameters now instead of inside them.
        Assert.Equal(IProductionScheduleService.DefaultMinutesPerWorkingDay, fake.LastMinutesPerWorkingDay);
    }
}
