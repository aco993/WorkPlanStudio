using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// One Playwright instance and one browser per test class. The base URL points at an
/// already-running app (E2E_BASE_URL, default localhost:5235); set HEADED=1 to
/// watch the browser locally.
///
/// This is an <c>IClassFixture</c>, not a collection fixture, on purpose: xUnit
/// parallelises across collections and a class without an explicit
/// <c>[Collection]</c> is its own collection. Each class therefore gets its own
/// browser and the classes run concurrently, while the tests inside one class
/// stay sequential. Every test still opens its own <see cref="IBrowserContext"/>,
/// so storage, the SQLite snapshot and the persona remain isolated — the browser
/// is shared, the state is not. <c>xunit.runner.json</c> caps how many classes
/// run at once so a two-core runner is not oversubscribed.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    private IPlaywright _playwright = null!;

    public IBrowser Browser { get; private set; } = null!;

    public string BaseUrl { get; } =
        (Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5235").TrimEnd('/');

    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("HEADED") != "1"
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
    }
}
