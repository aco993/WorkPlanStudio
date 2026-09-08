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

        // The flow factor belongs to the TWK target rule. Orders now carry real
        // customer due dates and Explicit is the default, so this test has to
        // select the rule whose knob it is about to turn.
        await schedule.SetDueRuleAsync("TotalWorkContent");

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

        // With the optimiser on, a good search converges different starting rules
        // onto the same near-optimal schedule, so the rule's own effect is only
        // observable with multi-start and local search switched off.
        await schedule.UseRuleOnlyAsync();
        await schedule.GenerateAsync();

        int barsBefore = await schedule.GanttBars.CountAsync();
        var makespanBefore = await schedule.MakespanTextAsync();   // EDD (the form default)

        await schedule.SetDispatchRuleAsync("LongestProcessingTime");
        await schedule.GenerateAsync();

        // every operation is still scheduled (a different sequence, but all of them) …
        Assert.Equal(barsBefore, await schedule.GanttBars.CountAsync());
        // … and the rule visibly changes the result (different makespan).
        Assert.NotEqual(makespanBefore, await schedule.MakespanTextAsync());
    }

    /// <summary>
    /// The optimiser is meant to make the starting rule largely irrelevant — that
    /// is the point of it, so it is worth asserting rather than assuming.
    /// </summary>
    [Fact]
    public async Task The_optimiser_converges_different_rules_onto_the_same_schedule()
    {
        var (context, schedule) = await OpenAsync();
        await using var _ = context;

        await schedule.SetDispatchRuleAsync("EarliestDueDate");
        await schedule.GenerateAsync();
        var fromEdd = await schedule.MakespanTextAsync();

        await schedule.SetDispatchRuleAsync("LongestProcessingTime");
        await schedule.GenerateAsync();

        Assert.Equal(fromEdd, await schedule.MakespanTextAsync());
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
    public async Task The_chat_answers_questions_about_the_schedule_in_english_and_german()
    {
        var (context, schedule) = await OpenAsync();
        await using var _ = context;
        var page = schedule.Page;

        // A suggested question, answered on-device from the schedule on the page.
        await page.Locator(".chat-suggestions .chip").First.ClickAsync();
        await page.Locator(".chat-turn.assistant").First.WaitForAsync();
        var answer = await page.Locator(".chat-turn.assistant .chat-bubble").First.InnerTextAsync();
        Assert.Contains("bottleneck", answer);
        Assert.Matches(@"[A-Z]{2,4}-\d+", answer);   // names a work center
        Assert.Equal("On-device", await page.Locator(".chat-turn.assistant .chat-source").First.InnerTextAsync());

        // A typed what-if actually re-runs the scheduler and compares.
        await page.FillAsync("#chat-question", "what if I use SPT?");
        await page.PressAsync("#chat-question", "Enter");
        await page.Locator(".chat-turn.assistant").Nth(1).WaitForAsync();
        var whatIf = await page.Locator(".chat-turn.assistant .chat-bubble").Nth(1).InnerTextAsync();
        Assert.Contains("SPT", whatIf);
        Assert.Contains("makespan", whatIf);

        // Switching the language resets the run and the thread; the answer comes back in German.
        await schedule.SwitchToGermanAsync();
        await page.Locator(".chat-suggestions .chip").First.ClickAsync();
        await page.Locator(".chat-turn.assistant").First.WaitForAsync();
        var german = await page.Locator(".chat-turn.assistant .chat-bubble").First.InnerTextAsync();
        Assert.Contains("Engpass", german);
        Assert.Equal("Auf dem Gerät", await page.Locator(".chat-turn.assistant .chat-source").First.InnerTextAsync());
    }

    [Fact]
    public async Task The_page_can_be_switched_to_german()
    {
        var (context, schedule) = await OpenAsync();
        await using var _ = context;

        await schedule.SwitchToGermanAsync();

        Assert.Contains("Produktionsplanung", await schedule.Heading.InnerTextAsync());
        Assert.Equal("de", await schedule.DocumentLanguageAsync());
    }

    [Fact]
    public async Task A_work_plan_can_be_created_without_blurring_the_last_edited_field()
    {
        var context = await _fixture.Browser.NewContextAsync();
        await using var _ = context;
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/work-plans/new");
        await page.GetByRole(AriaRole.Heading, new() { Name = "New work plan" }).WaitForAsync();

        await page.GetByLabel("Part name").FillAsync("E2E review part");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add operation" }).ClickAsync();

        var row = page.Locator(".op-table tbody tr");
        await row.Locator("select").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await row.Locator("input").Nth(1).FillAsync("Assembly step");
        await row.Locator("input").Nth(2).FillAsync("10");
        await row.Locator("input").Nth(3).FillAsync("1");

        await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await page.WaitForURLAsync("**/work-plans");
        var createdPlan = page.GetByText("E2E review part");
        await createdPlan.WaitForAsync();
        Assert.Equal(1, await createdPlan.CountAsync());

        await page.ReloadAsync();
        var headingAfterReload = page.GetByRole(AriaRole.Heading).First;
        await headingAfterReload.WaitForAsync(new() { Timeout = 60_000 });
        Assert.Equal("Work Plans", await headingAfterReload.InnerTextAsync());
        Assert.Equal(1, await page.GetByText("E2E review part").CountAsync());
    }

    [Fact]
    public async Task Invalid_work_plan_is_not_saved_and_does_not_crash_the_app()
    {
        var context = await _fixture.Browser.NewContextAsync();
        await using var _ = context;
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/work-plans/new");
        await page.GetByRole(AriaRole.Heading, new() { Name = "New work plan" }).WaitForAsync();

        await page.GetByLabel("Part name").FillAsync("Invalid E2E part");
        await page.GetByLabel("Lot size").FillAsync("-1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add operation" }).ClickAsync();
        var row = page.Locator(".op-table tbody tr");
        await row.Locator("select").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await row.Locator("input").Nth(1).FillAsync("Invalid step");

        await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        Assert.EndsWith("/work-plans/new", page.Url, StringComparison.Ordinal);
        await page.GetByText("Lot size must be between 1 and 1000000.").WaitForAsync();
        Assert.Equal(0, await page.Locator("#blazor-error-ui.show").CountAsync());
    }

    [Fact]
    public async Task Work_center_modal_supports_dialog_semantics_escape_and_focus_return()
    {
        var context = await _fixture.Browser.NewContextAsync();
        await using var _ = context;
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/work-centers");
        var open = page.GetByRole(AriaRole.Button, new() { Name = "New work center" });
        await open.ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "New work center" });
        await dialog.WaitForAsync();
        Assert.Equal("true", await dialog.GetAttributeAsync("aria-modal"));
        await page.Keyboard.PressAsync("Escape");

        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.True(await open.EvaluateAsync<bool>("element => element === document.activeElement"));
    }

    [Fact]
    public async Task Reset_reseeds_only_after_confirmation_and_removes_local_changes()
    {
        var context = await _fixture.Browser.NewContextAsync();
        await using var _ = context;
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/work-centers");
        await page.GetByRole(AriaRole.Button, new() { Name = "New work center" }).ClickAsync();
        var editor = page.GetByRole(AriaRole.Dialog, new() { Name = "New work center" });
        await editor.GetByLabel("Code").FillAsync("RESET-E2E");
        await editor.GetByLabel("Name").FillAsync("Reset regression");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await page.GetByText("RESET-E2E").WaitForAsync();

        await page.GotoAsync($"{_fixture.BaseUrl}/about");
        await page.GetByRole(AriaRole.Button, new() { Name = "Reset to sample data" }).ClickAsync();
        var confirmation = page.GetByRole(AriaRole.Dialog, new() { Name = "Reset to sample data" });
        Assert.Contains("Discard your changes", await confirmation.InnerTextAsync());
        await confirmation.GetByRole(AriaRole.Button, new() { Name = "Reset to sample data" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" }).WaitForAsync(new() { Timeout = 60_000 });

        await page.GotoAsync($"{_fixture.BaseUrl}/work-centers");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Work Centers" }).WaitForAsync();
        Assert.Equal(0, await page.GetByText("RESET-E2E").CountAsync());
    }

    [Fact]
    public async Task The_gantt_shades_closed_time_with_a_reason_and_the_axis_shows_dates()
    {
        var (context, schedule) = await OpenAsync();
        await using var _ = context;

        // The seed plant sits in NW and the horizon is the week of Fronleichnam:
        // the holiday, a Sunday, a break and the grinder's service all show.
        var page = schedule.Page;
        await page.Locator(".gantt-closed.closed-holiday").First.WaitForAsync();
        Assert.Contains("Corpus Christi", await page.Locator(".gantt-closed.closed-holiday").First.GetAttributeAsync("title") ?? "");
        Assert.True(await page.Locator(".gantt-closed.closed-sunday").CountAsync() > 0, "expected Sunday shading");
        Assert.True(await page.Locator(".gantt-closed.closed-break").CountAsync() > 0, "expected break shading");
        Assert.Contains("Spindle service", await page.Locator(".gantt-closed.closed-absence").First.GetAttributeAsync("title") ?? "");
        Assert.True(await page.Locator(".gantt-bar.paused").CountAsync() > 0, "expected an operation paused across a break");

        var ticks = await page.Locator(".gantt-tick").AllInnerTextsAsync();
        Assert.Contains(ticks, t => t.Contains("Thu 4.6.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_the_state_changes_the_holidays_and_the_schedule()
    {
        var context = await _fixture.Browser.NewContextAsync();
        await using var _ = context;
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/working-time");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Working Time & Labour Law" }).WaitForAsync(new() { Timeout = 60_000 });

        // NW today: Corpus Christi is in the table; Berlin: it is not, Women's Day is.
        await page.GetByText("Corpus Christi").First.WaitForAsync();
        await page.Locator("#wt-state").SelectOptionAsync("BE");
        await page.GetByText("International Women's Day").WaitForAsync();
        Assert.Equal(0, await page.GetByText("Corpus Christi").CountAsync());

        // The live preview explains the cuts the rules make.
        Assert.Contains("of break carved out of", await page.Locator(".wt-applications").InnerTextAsync());

        await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await page.GetByText("Plant rules saved").WaitForAsync();

        // Berlin has no holiday on Thursday 4 June, so the schedule no longer shades one.
        var schedule = new SchedulePage(page, _fixture.BaseUrl);
        await schedule.GotoAsync();
        Assert.Equal(0, await page.Locator(".gantt-closed.closed-holiday").CountAsync());
    }

    [Fact]
    public async Task Switching_persona_changes_what_the_pages_allow()
    {
        var context = await _fixture.Browser.NewContextAsync();
        await using var _ = context;
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/work-centers");
        var newCenter = page.GetByRole(AriaRole.Button, new() { Name = "New work center" });
        await newCenter.WaitForAsync(new() { Timeout = 60_000 });   // a first visit is the planner

        // Guest: the action disappears and a notice explains who could do it.
        // (<summary> is not a button in the accessibility tree, so it is addressed by class.)
        await page.Locator("summary.persona-summary").ClickAsync();
        await page.GetByRole(AriaRole.Menuitemradio, new() { Name = "Guest" }).ClickAsync();
        await newCenter.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.GetByText("You are working as Guest").WaitForAsync();

        // The persona survives a full reload …
        await page.ReloadAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Work Centers" }).WaitForAsync(new() { Timeout = 60_000 });
        await page.GetByText("You are working as Guest").WaitForAsync();
        Assert.Equal(0, await newCenter.CountAsync());

        // … and a supervisor may release orders but still not edit master data.
        await page.Locator("summary.persona-summary").ClickAsync();
        await page.GetByRole(AriaRole.Menuitemradio, new() { Name = "Supervisor" }).ClickAsync();
        await page.GetByText("You are working as Supervisor").WaitForAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/production-orders");
        await page.GetByRole(AriaRole.Button, new() { Name = "New order" }).WaitForAsync(new() { Timeout = 60_000 });
        Assert.Equal(0, await page.GetByText("You are working as Supervisor").CountAsync());
    }

    [Fact]
    public async Task The_theme_toggle_switches_to_dark_and_survives_a_reload()
    {
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions { ColorScheme = ColorScheme.Light });
        await using var _ = context;
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" }).WaitForAsync(new() { Timeout = 60_000 });
        Assert.Equal("light", await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme"));

        var toggle = page.Locator(".theme-toggle");
        await toggle.ClickAsync();   // system → light
        await toggle.ClickAsync();   // light → dark
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme"));

        // The page background actually changed, not just an attribute.
        var background = await page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");
        Assert.Equal("rgb(15, 17, 23)", background);

        await page.ReloadAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" }).WaitForAsync(new() { Timeout = 60_000 });
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => document.documentElement.dataset.theme"));
    }

    [Fact]
    public async Task Keyboard_users_can_skip_to_the_content_and_tab_stays_inside_a_dialog()
    {
        var context = await _fixture.Browser.NewContextAsync();
        await using var _ = context;
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/work-centers");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Work Centers" }).WaitForAsync(new() { Timeout = 60_000 });

        // The skip link is the first element in the document. It is off-screen
        // until focused, becomes visible on focus, and Enter moves focus to <main>.
        // (Blazor's FocusOnNavigate parks focus on the heading after a route
        // change, so the link is reached from the document start, as a keyboard
        // user arriving from the address bar would.)
        var skip = page.Locator(".skip-link");
        Assert.True(await skip.EvaluateAsync<bool>("el => el.getBoundingClientRect().bottom < 0"), "skip link should be hidden until focused");
        await skip.FocusAsync();
        Assert.True(await skip.EvaluateAsync<bool>("el => el.getBoundingClientRect().top >= 0"), "skip link should be visible when focused");
        Assert.Equal("Skip to content", await page.EvaluateAsync<string>("() => document.activeElement.textContent.trim()"));
        await page.Keyboard.PressAsync("Enter");
        Assert.Equal("main", await page.EvaluateAsync<string>("() => document.activeElement.id"));

        // Inside a dialog, Tab wraps instead of escaping to the page behind.
        await page.GetByRole(AriaRole.Button, new() { Name = "New work center" }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "New work center" });
        await dialog.WaitForAsync();
        for (int i = 0; i < 12; i++)
            await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("() => !!document.activeElement.closest('[role=dialog]')"), "focus escaped the dialog");
    }

    [Fact]
    public async Task Mobile_drawer_navigation_keeps_the_core_flow_usable()
    {
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }
        });
        await using var _ = context;
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/schedule");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Production Scheduling" }).WaitForAsync(new() { Timeout = 60_000 });

        await page.GetByRole(AriaRole.Button, new() { Name = "Menu" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Work Centers" }).ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Work Centers" }).WaitForAsync();
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth"));
    }
}
