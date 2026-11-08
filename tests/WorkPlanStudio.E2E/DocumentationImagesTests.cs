using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// Recaptures the screenshots the README shows, so they can be refreshed rather
/// than quietly going stale.
/// </summary>
/// <remarks>
/// <para>
/// The README's images had no producer. They were taken once with Playwright
/// against a build of the day, and after that a change to the page left them
/// showing an application that no longer exists — which is exactly what happened
/// when the parameter rail moved to the right: four pictures went on claiming it
/// was on the left, in the one document a reader looks at before anything else.
/// Visual baselines have had a refresh path since ADR 0021; the documentation
/// did not.
/// </para>
/// <para>
/// Linux only, for ADR 0021's reason: these end up side by side in one page, and
/// a set captured on three operating systems reads as three applications. CI is
/// <c>ubuntu-latest</c>, so that is where they come from.
/// </para>
/// <para>
/// Skipped unless <c>CAPTURE_DOC_IMAGES=1</c>. It writes into <c>docs/</c>, which
/// no ordinary test run has any business doing.
/// </para>
/// </remarks>
public sealed class DocumentationImagesTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture;

    public DocumentationImagesTests(PlaywrightFixture fixture) => _fixture = fixture;

    private static bool Capturing =>
        Environment.GetEnvironmentVariable("CAPTURE_DOC_IMAGES") == "1";

    private static string DocsDirectory =>
        Path.GetFullPath(Path.Combine(VisualRegressionTests.RepositoryRoot(), "docs"));

    /// <summary>The viewports the README's layout was written around.</summary>
    private static (int Width, int Height) Desktop => (1320, 980);
    private static (int Width, int Height) Phone => (390, 844);

    private async Task<IPage> OpenAsync((int Width, int Height) viewport, string theme)
    {
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = viewport.Width, Height = viewport.Height },
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
            // The same four settings the visual baselines pin, for the same
            // reason: a screenshot that moves with the machine is not evidence.
            ReducedMotion = ReducedMotion.Reduce,
            DeviceScaleFactor = 1,
            Locale = "en-US",
            TimezoneId = "Europe/Berlin"
        });

        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/");
        await page.EvaluateAsync("t => localStorage.setItem('workplanstudio.settings.theme', t)", theme);
        return page;
    }

    private static async Task SaveAsync(IPage page, string relativePath)
    {
        var target = Path.Combine(DocsDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await page.ScreenshotAsync(new()
        {
            Path = target,
            FullPage = true,
            Animations = ScreenshotAnimations.Disabled,
            Caret = ScreenshotCaret.Hide
        });
        Xunit.TestContext.Current.SendDiagnosticMessage($"documentation image written: {target}");
    }

    [Fact]
    public async Task Capture_the_screenshots_the_readme_shows()
    {
        Assert.SkipUnless(Capturing,
            "set CAPTURE_DOC_IMAGES=1 to rewrite the README's screenshots; " +
            "an ordinary run must not write into docs/.");
        Assert.SkipUnless(VisualRegressionTests.BaselinesAreMaintainedHere,
            $"documentation images are captured on {string.Join("/", VisualRegressionTests.MaintainedBaselineDirectories)} " +
            $"only, for the reason in ADR 0021; this run is on {VisualRegressionTests.CurrentOperatingSystem}.");

        // --- the schedule, dark, as the README's screenshot table shows it ---
        var dark = await OpenAsync(Desktop, "dark");
        var darkSchedule = new SchedulePage(dark, _fixture.BaseUrl);
        await darkSchedule.GotoAsync();
        await AppReady.SettledAsync(dark);
        await SaveAsync(dark, Path.Combine("images", "schedule-dark.png"));

        // --- the schedule on a phone ---
        var phone = await OpenAsync(Phone, "light");
        var phoneSchedule = new SchedulePage(phone, _fixture.BaseUrl);
        await phoneSchedule.GotoAsync();
        await AppReady.SettledAsync(phone);
        await SaveAsync(phone, Path.Combine("images", "schedule-mobile.png"));

        // --- the pair the README uses to show one parameter changing everything ---
        //
        // Same seven orders, same seed, same rule: only the flow factor moves.
        // At 5.0 every order is on time; at 0.5 all seven are late. The two
        // pictures are only an argument while they differ in exactly that.
        var light = await OpenAsync(Desktop, "light");
        var schedule = new SchedulePage(light, _fixture.BaseUrl);
        await schedule.GotoAsync();
        await schedule.SetDueRuleAsync("Twk");

        await schedule.SetFlowFactorAsync("5.0");
        await schedule.GenerateAsync();
        await AppReady.SettledAsync(light);
        Assert.Equal(0, await schedule.LateBars.CountAsync());
        await SaveAsync(light, "schedule-ontime.png");

        await schedule.SetFlowFactorAsync("0.5");
        await schedule.GenerateAsync();
        await AppReady.SettledAsync(light);
        Assert.True(await schedule.LateBars.CountAsync() > 0,
            "the 'late' picture has nothing late in it; the pair stops being an argument");
        await SaveAsync(light, "schedule-late.png");
    }
}
