using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// Visual regression: full-page screenshots of the key screens, in three
/// profiles, compared pixel-wise against baselines committed per operating
/// system (fonts and anti-aliasing differ between Windows and Linux, so one
/// baseline cannot serve both). The comparison runs inside the browser on two
/// canvases — no image library, no native dependency.
///
/// - A missing baseline is written and the test passes with a note, so a new
///   profile or a new OS bootstraps itself; the CI job uploads the folder so
///   the baselines can be committed.
/// - <c>VISUAL_UPDATE=1</c> rewrites every baseline (after an intended change).
/// - The diff image of a failure lands next to the baseline as <c>*.diff.png</c>
///   and in <c>E2E_ARTIFACTS</c>.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public sealed class VisualRegressionTests
{
    /// <summary>Share of pixels allowed to differ beyond the per-channel tolerance.</summary>
    private const double AllowedDifferentPixelShare = 0.002;

    private readonly PlaywrightFixture _fixture;

    public VisualRegressionTests(PlaywrightFixture fixture) => _fixture = fixture;

    public static TheoryData<string, string, string> Screens()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var route in new[] { "/", "/schedule", "/working-time" })
        {
            data.Add(route, "desktop", "light");
            data.Add(route, "desktop", "dark");
            data.Add(route, "mobile", "light");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task The_screen_matches_its_baseline(string route, string device, string theme)
    {
        var (width, height) = device == "mobile" ? (390, 844) : (1320, 980);
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce,
            DeviceScaleFactor = 1,
            Locale = "en-US",
            TimezoneId = "Europe/Berlin"
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{_fixture.BaseUrl}/");
        await page.EvaluateAsync("t => localStorage.setItem('workplanstudio.settings.theme', t)", theme);
        await page.GotoAsync($"{_fixture.BaseUrl}{route}");
        await page.WaitForSelectorAsync("main h1", new() { Timeout = 60_000 });
        await page.WaitForSelectorAsync(".gantt, .empty-state, .data-table, .glance-kpis, .form-grid", new() { Timeout = 60_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.EvaluateAsync("document.fonts.ready.then(() => true)");
        await page.WaitForTimeoutAsync(400);

        var name = $"{(route == "/" ? "home" : route.Trim('/').Replace('/', '-'))}-{device}-{theme}";
        var baselinePath = Path.Combine(BaselineDirectory, name + ".png");
        var current = await page.ScreenshotAsync(new() { FullPage = true, Animations = ScreenshotAnimations.Disabled, Caret = ScreenshotCaret.Hide });

        if (!File.Exists(baselinePath) || Environment.GetEnvironmentVariable("VISUAL_UPDATE") == "1")
        {
            Directory.CreateDirectory(BaselineDirectory);
            await File.WriteAllBytesAsync(baselinePath, current, Xunit.TestContext.Current.CancellationToken);
            Xunit.TestContext.Current.SendDiagnosticMessage($"visual baseline written: {baselinePath}");
            return;
        }

        var baseline = await File.ReadAllBytesAsync(baselinePath, Xunit.TestContext.Current.CancellationToken);
        var diff = await CompareAsync(page, baseline, current);

        if (diff.DifferentShare > AllowedDifferentPixelShare || diff.SizeDiffers)
        {
            var artifacts = Environment.GetEnvironmentVariable("E2E_ARTIFACTS") ?? BaselineDirectory;
            Directory.CreateDirectory(artifacts);
            await File.WriteAllBytesAsync(Path.Combine(artifacts, name + ".actual.png"), current, Xunit.TestContext.Current.CancellationToken);
            if (diff.DiffPng is not null)
                await File.WriteAllBytesAsync(Path.Combine(artifacts, name + ".diff.png"), diff.DiffPng, Xunit.TestContext.Current.CancellationToken);
        }

        Assert.False(diff.SizeDiffers, $"{name}: page size changed from {diff.BaselineSize} to {diff.CurrentSize} (set VISUAL_UPDATE=1 after an intended change)");
        Assert.True(diff.DifferentShare <= AllowedDifferentPixelShare,
            $"{name}: {diff.DifferentShare:P2} of pixels differ (allowed {AllowedDifferentPixelShare:P2}); see {name}.diff.png (set VISUAL_UPDATE=1 after an intended change)");
    }

    private static string BaselineDirectory { get; } =
        Path.Combine(RepositoryRoot(), "tests", "WorkPlanStudio.E2E", "visual-baselines", OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkPlanStudio.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }

    private sealed record Comparison(bool SizeDiffers, string BaselineSize, string CurrentSize, double DifferentShare, byte[]? DiffPng);

    /// <summary>Decodes both PNGs on canvases in the page and counts pixels whose channels differ by more than 24/255.</summary>
    private static async Task<Comparison> CompareAsync(IPage page, byte[] baseline, byte[] current)
    {
        var result = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            async ([a, b]) => {
              const load = src => new Promise((resolve, reject) => { const i = new Image(); i.onload = () => resolve(i); i.onerror = reject; i.src = 'data:image/png;base64,' + src; });
              const [ia, ib] = await Promise.all([load(a), load(b)]);
              if (ia.width !== ib.width || ia.height !== ib.height)
                return { sizeDiffers: true, baselineSize: ia.width + 'x' + ia.height, currentSize: ib.width + 'x' + ib.height, share: 1, diff: null };
              const draw = img => { const c = document.createElement('canvas'); c.width = img.width; c.height = img.height; const x = c.getContext('2d'); x.drawImage(img, 0, 0); return x.getImageData(0, 0, img.width, img.height); };
              const da = draw(ia), db = draw(ib);
              const out = document.createElement('canvas'); out.width = ia.width; out.height = ia.height;
              const ox = out.getContext('2d'); const od = ox.createImageData(ia.width, ia.height);
              let different = 0; const n = da.data.length / 4;
              for (let i = 0; i < n; i++) {
                const p = i * 4;
                const d = Math.max(Math.abs(da.data[p] - db.data[p]), Math.abs(da.data[p+1] - db.data[p+1]), Math.abs(da.data[p+2] - db.data[p+2]));
                if (d > 24) { different++; od.data[p] = 255; od.data[p+1] = 0; od.data[p+2] = 0; od.data[p+3] = 255; }
                else { const g = Math.round((db.data[p] + db.data[p+1] + db.data[p+2]) / 3); od.data[p] = g; od.data[p+1] = g; od.data[p+2] = g; od.data[p+3] = 80; }
              }
              ox.putImageData(od, 0, 0);
              return { sizeDiffers: false, baselineSize: ia.width + 'x' + ia.height, currentSize: ib.width + 'x' + ib.height, share: different / n, diff: out.toDataURL('image/png').split(',')[1] };
            }
            """,
            new[] { Convert.ToBase64String(baseline), Convert.ToBase64String(current) });

        var diff = result.GetProperty("diff");
        return new Comparison(
            result.GetProperty("sizeDiffers").GetBoolean(),
            result.GetProperty("baselineSize").GetString() ?? "",
            result.GetProperty("currentSize").GetString() ?? "",
            result.GetProperty("share").GetDouble(),
            diff.ValueKind == System.Text.Json.JsonValueKind.String ? Convert.FromBase64String(diff.GetString()!) : null);
    }
}
