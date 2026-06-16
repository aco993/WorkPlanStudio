using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// Shared Playwright + browser for the whole E2E run. The base URL points at an
/// already-running app (E2E_BASE_URL, default localhost:5235); set HEADED=1 to
/// watch the browser locally.
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

[CollectionDefinition(nameof(PlaywrightCollection))]
public sealed class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>;
