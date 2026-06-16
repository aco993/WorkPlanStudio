using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>Page object for <c>/schedule</c> — keeps selectors and actions in one place.</summary>
public sealed class SchedulePage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public SchedulePage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    public ILocator Heading => _page.Locator(".page-head h1");
    public ILocator KpiCards => _page.Locator(".stat-card");
    public ILocator GanttBars => _page.Locator(".gantt-bar");
    public ILocator LateBars => _page.Locator(".gantt-bar.late");
    public ILocator LatePills => _page.Locator(".pill.late");
    public ILocator JobRows => _page.Locator(".data-table tbody tr");

    public async Task GotoAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/schedule");
        // wait for the WASM app to boot and the first schedule to render
        await _page.WaitForSelectorAsync(".gantt, .empty-state", new() { Timeout = 60_000 });
    }

    // Parameter fields, in DOM order: 1 dispatch rule, 2 target-date rule,
    // 3 (conditional) factor, 4 multi-start, 5 local search, 6 seed, 7 minutes/day.
    public Task SetDispatchRuleAsync(string enumName) =>
        _page.Locator(".param-grid label:nth-of-type(1) select").SelectOptionAsync(new SelectOptionValue { Value = enumName });

    public Task SetDueRuleAsync(string enumName) =>
        _page.Locator(".param-grid label:nth-of-type(2) select").SelectOptionAsync(new SelectOptionValue { Value = enumName });

    public Task SetFlowFactorAsync(string value) =>
        _page.FillAsync(".param-grid label:nth-of-type(3) input.num", value);

    public Task SetSeedAsync(string value) =>
        _page.FillAsync(".param-grid label:nth-of-type(6) input.num", value);

    public async Task GenerateAsync()
    {
        await _page.ClickAsync(".btn-primary");
        // the button is disabled while the engine runs; wait until it is enabled again
        await _page.Locator(".btn-primary:not([disabled])").WaitForAsync(new() { Timeout = 30_000 });
    }

    public async Task SwitchToGermanAsync()
    {
        await _page.GetByRole(AriaRole.Button, new() { Name = "DE" }).ClickAsync();
        await _page.WaitForSelectorAsync(".gantt, .empty-state", new() { Timeout = 60_000 });
    }

    public Task<string> MakespanTextAsync() => _page.Locator(".stat-value").First.InnerTextAsync();

    public async Task ScreenshotAsync(string fileName)
    {
        var dir = Environment.GetEnvironmentVariable("E2E_ARTIFACTS") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);
        await _page.ScreenshotAsync(new() { Path = Path.Combine(dir, fileName), FullPage = true });
    }
}
