using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// The same axe bar, applied to the German UI. Bilingualism is a headline
/// feature and German copy is materially longer than English, so it is the
/// language more likely to overflow a control, truncate a label or push a
/// contrast-sensitive badge onto two lines — yet before this class no German
/// page had ever been contrast- or label-checked.
///
/// Light theme only: the dark palette is a token swap that does not depend on
/// the culture, and it is already covered for every route in English. What is
/// culture-dependent is the text, and that is what this scans.
/// </summary>
public sealed class GermanAccessibilityE2ETests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture;

    public GermanAccessibilityE2ETests(PlaywrightFixture fixture) => _fixture = fixture;

    public static TheoryData<string> GermanRoutes()
    {
        var data = new TheoryData<string>();
        foreach (var route in AccessibilityE2ETests.Routes)
            data.Add(route);
        return data;
    }

    [Theory]
    [MemberData(nameof(GermanRoutes))]
    public async Task Every_german_page_has_no_wcag_aa_violations(string route)
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1320, Height = 980 },
            ColorScheme = ColorScheme.Light
        });
        var page = await context.NewPageAsync();

        // Program.cs reads the stored culture on startup, so it has to be in
        // place before the app boots — the same trick the theme scan uses.
        await page.GotoAsync($"{_fixture.BaseUrl}/");
        await page.EvaluateAsync("() => localStorage.setItem('BlazorCulture', 'de-DE')");
        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}{route}");
        await page.WaitForSelectorAsync(".gantt, .empty-state, .data-table, .glance-kpis, .about-grid, .form-grid, .param-grid", new() { Timeout = AppReady.BootTimeoutMilliseconds });
        await AppReady.SettledAsync(page);

        // Guard the premise: a scan of an English page that silently failed to
        // switch culture would pass and prove nothing.
        Assert.Equal("de", await page.Locator("html").GetAttributeAsync("lang"));

        await AccessibilityE2ETests.AssertNoViolationsAsync(page, $"{route} (de)", route);
    }
}
