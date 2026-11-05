using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// WCAG 2.2 AA 2.5.8 Target Size (Minimum), measured — because axe cannot.
/// </summary>
/// <remarks>
/// <para>
/// axe-core has no rule for 2.5.8. The accessibility suite next door can run
/// every route in both themes and both languages and come back clean while a
/// control is three pixels wide, which is exactly what it did: measured on the
/// published site at 375 x 812, <b>109</b> interactive elements sat under
/// 24 x 24 CSS px. A green automated run is a floor, and this is the part of the
/// floor that run cannot reach.
/// </para>
/// <para>
/// The phone viewport is the one that matters here. The rest of the suite scans
/// at 1320 x 980, where the chart has room; a target shrinks when the page does.
/// </para>
/// </remarks>
public sealed class TargetSizeE2ETests : IClassFixture<PlaywrightFixture>
{
    private const int Minimum = 24;

    private readonly PlaywrightFixture _fixture;

    public TargetSizeE2ETests(PlaywrightFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The Gantt segments, which 2.5.8 excepts rather than excuses.
    /// </summary>
    /// <remarks>
    /// A bar's width <em>is</em> the operation's duration and a closed stretch's
    /// width is how long the work centre stood still. Widening a sliver to reach
    /// 24 px would not make the chart more usable, it would make it say something
    /// untrue — which is the <b>Essential</b> exception, almost to the letter.
    /// The <b>Equivalent</b> exception covers it a second time: the same data is
    /// a table on the same page, one visible toggle away, and that table's
    /// controls are full size.
    /// <para>
    /// Their <em>height</em> had no such excuse and was simply 20 px, so the lane
    /// is taller now and the bars clear the floor in the one dimension that was
    /// never carrying information. The exception below is therefore about width
    /// alone, and the assertion still holds these elements to 24 px vertically.
    /// </para>
    /// </remarks>
    private const string ExceptedByWidth = ".gantt-bar, .gantt-closed";

    public static TheoryData<string> Routes()
    {
        var data = new TheoryData<string>();
        foreach (var route in AccessibilityE2ETests.Routes)
            data.Add(route);
        return data;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_target_is_large_enough_on_a_phone(string route)
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 375, Height = 812 }
        });
        var page = await context.NewPageAsync();

        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}{route}");
        await page.WaitForSelectorAsync(AppReady.ContentSelector, new() { Timeout = AppReady.BootTimeoutMilliseconds });
        await AppReady.SettledAsync(page);

        var undersized = await page.EvaluateAsync<string[]>(
            """
            excepted => {
                const selector = 'a[href], button, select, textarea, summary, [role=button], ' +
                                 'input:not([type=hidden])';
                const offenders = [];
                for (const element of document.querySelectorAll(selector)) {
                    // The target is the region that accepts the pointer, not the
                    // painted control: a checkbox wrapped in its own <label> is
                    // operated by clicking the words too, so the label is what
                    // 2.5.8 measures. Measuring the 13px box instead reports five
                    // defects this application does not have.
                    const label = element.closest('label');
                    const box = (label && element.matches('input, select, textarea'))
                        ? label.getBoundingClientRect()
                        : element.getBoundingClientRect();
                    // Not rendered at all: nothing to hit, and not this rule's business.
                    if (box.width === 0 || box.height === 0) continue;
                    const style = getComputedStyle(element);
                    if (style.visibility === 'hidden' || style.display === 'none') continue;

                    // The width of these carries the data; their height does not.
                    const widthExcepted = element.matches(excepted);
                    const tooNarrow = !widthExcepted && box.width < 24;
                    const tooShort = box.height < 24;
                    if (!tooNarrow && !tooShort) continue;

                    const name = (element.getAttribute('aria-label')
                               || element.textContent.trim()
                               || element.className
                               || element.tagName).slice(0, 60);
                    offenders.push(
                        `${Math.round(box.width)}x${Math.round(box.height)} ${element.tagName.toLowerCase()} "${name}"`);
                }
                // One line per distinct shape: forty identical bars are one defect.
                return [...new Set(offenders)];
            }
            """,
            ExceptedByWidth);

        Assert.True(
            undersized.Length == 0,
            $"{route} at 375x812 has {undersized.Length} target(s) under {Minimum} CSS px "
            + $"(the Gantt segments are excepted on width only — see the remarks on {nameof(ExceptedByWidth)}):\n  "
            + string.Join("\n  ", undersized));
    }

    /// <summary>
    /// The equivalent control 2.5.8 lets the chart lean on has to actually be there.
    /// </summary>
    /// <remarks>
    /// Claiming the Equivalent exception for the Gantt is only honest while the
    /// table exists, says the same thing and is reachable — so that is asserted
    /// rather than asserted-in-a-comment. The toggle is in the DOM at every
    /// width; the table it reveals carries a row per operation.
    /// </remarks>
    [Fact]
    public async Task The_chart_has_a_table_a_phone_can_reach_and_operate()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 375, Height = 812 }
        });
        var page = await context.NewPageAsync();

        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}/schedule");
        await page.WaitForSelectorAsync(".gantt", new() { Timeout = AppReady.BootTimeoutMilliseconds });
        await AppReady.SettledAsync(page);

        var toggle = page.Locator(".gantt-table-toggle");
        await toggle.WaitForAsync(new() { Timeout = AppReady.BootTimeoutMilliseconds });

        var box = await toggle.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.Width >= Minimum && box.Height >= Minimum,
            $"the chart's table toggle is {box.Width}x{box.Height}; the exception the chart "
            + "relies on is only real while the way to the table is itself a usable target");

        await toggle.ClickAsync();
        await page.WaitForSelectorAsync("#gantt-table table", new() { Timeout = AppReady.BootTimeoutMilliseconds });

        var rows = await page.Locator("#gantt-table tbody tr").CountAsync();
        Assert.True(rows > 0, "the table the chart points at came up empty");
    }
}
