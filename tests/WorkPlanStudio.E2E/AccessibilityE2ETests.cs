using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// Automated WCAG checks with axe-core against the running app: every route,
/// in the light and the dark theme, must produce zero violations of the
/// WCAG 2.x A/AA rule set. Automated rules catch roughly a third of WCAG —
/// contrast, names, roles, landmarks, headings, form labels — so this is the
/// floor, not the ceiling; the keyboard and dialog flows are exercised in
/// <see cref="ScheduleE2ETests"/>.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public sealed class AccessibilityE2ETests
{
    private readonly PlaywrightFixture _fixture;

    public AccessibilityE2ETests(PlaywrightFixture fixture) => _fixture = fixture;

    /// <summary>The routes a visitor can reach from the navigation, plus the editor.</summary>
    public static TheoryData<string, string> RoutesAndThemes()
    {
        var data = new TheoryData<string, string>();
        foreach (var route in new[] { "/", "/schedule", "/production-orders", "/work-plans", "/work-plans/new", "/work-centers", "/working-time", "/about" })
        {
            data.Add(route, "light");
            data.Add(route, "dark");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RoutesAndThemes))]
    public async Task Every_page_has_no_wcag_aa_violations(string route, string theme)
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1320, Height = 980 },
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light
        });
        var page = await context.NewPageAsync();

        // Pick the theme the same way the app does, before Blazor boots.
        await page.GotoAsync($"{_fixture.BaseUrl}/");
        await page.EvaluateAsync("t => localStorage.setItem('workplanstudio.settings.theme', t)", theme);
        await page.GotoAsync($"{_fixture.BaseUrl}{route}");
        await page.WaitForSelectorAsync("main h1", new() { Timeout = 60_000 });
        await page.WaitForSelectorAsync(".gantt, .empty-state, .data-table, .glance-kpis, .about-grid, .form-grid, .param-grid", new() { Timeout = 60_000 });
        await page.WaitForTimeoutAsync(300);   // let the last async render settle

        var result = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"] }
        });

        var report = string.Join("\n", result.Violations.Select(v =>
            $"[{v.Impact}] {v.Id}: {v.Help}\n" + string.Join("\n", v.Nodes.Take(3).Select(n => $"    {n.Target}  {Shorten(n.Html)}"))));
        Assert.True(result.Violations.Length == 0, $"{route} ({theme}) has {result.Violations.Length} axe violation(s):\n{report}");
    }

    [Fact]
    public async Task An_open_dialog_has_no_wcag_aa_violations()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/work-centers");
        await page.WaitForSelectorAsync(".data-table", new() { Timeout = 60_000 });
        await page.Locator(".page-head .btn-primary").ClickAsync();
        await page.WaitForSelectorAsync("[role=dialog]");
        await page.WaitForTimeoutAsync(300);   // the dialog fades in; contrast is measured on the settled colours

        var result = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"] }
        });

        var report = string.Join("\n", result.Violations.Select(v =>
            $"[{v.Impact}] {v.Id}: {v.Help}\n" + string.Join("\n", v.Nodes.Take(3).Select(n => $"    {n.Target}  {Shorten(n.Html)}"))));
        Assert.True(result.Violations.Length == 0, $"dialog has {result.Violations.Length} axe violation(s):\n{report}");
    }

    private static string Shorten(string html) => html.Length <= 120 ? html : html[..120] + "…";
}
