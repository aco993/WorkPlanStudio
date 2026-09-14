using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// Visual regression: full-page screenshots of the key screens, in three
/// profiles, compared pixel-wise against committed baselines. The comparison
/// runs inside the browser on two canvases — no image library, no native
/// dependency.
///
/// <para><b>Baselines are maintained for Linux only</b> (see ADR 0021). Fonts and
/// anti-aliasing differ per operating system, so one baseline cannot serve
/// several; and a baseline that no automation ever compares is maintenance
/// without a consumer. CI runs <c>ubuntu-latest</c>, so <c>visual-baselines/linux</c>
/// is the one set anything checks and the one set that is committed. On any
/// other operating system these tests skip loudly rather than compare against
/// something unverified or quietly invent a new baseline.</para>
///
/// <para>On Linux a missing baseline is a <b>failure</b>, not a bootstrap: renaming
/// a route or a profile used to make its guard disappear silently. The actual
/// screenshot is still written to <c>E2E_ARTIFACTS</c> so the new baseline can be
/// downloaded and committed, and only an explicit
/// <c>UPDATE_VISUAL_BASELINES=1</c> (legacy alias: <c>VISUAL_UPDATE=1</c>)
/// writes into the committed folder.</para>
/// </summary>
public sealed class VisualRegressionTests : IClassFixture<PlaywrightFixture>
{
    /// <summary>Share of pixels allowed to differ beyond the per-channel tolerance.</summary>
    private const double AllowedDifferentPixelShare = 0.002;

    private readonly PlaywrightFixture _fixture;

    public VisualRegressionTests(PlaywrightFixture fixture) => _fixture = fixture;

    /// <summary>The screen/profile matrix, and the single source of the baseline file names.</summary>
    private static IEnumerable<(string Route, string Device, string Theme)> Matrix =>
        from route in new[] { "/", "/schedule", "/working-time" }
        from profile in new[] { ("desktop", "light"), ("desktop", "dark"), ("mobile", "light") }
        select (route, profile.Item1, profile.Item2);

    public static TheoryData<string, string, string> Screens()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (route, device, theme) in Matrix)
            data.Add(route, device, theme);
        return data;
    }

    private static string BaselineName(string route, string device, string theme) =>
        $"{(route == "/" ? "home" : route.Trim('/').Replace('/', '-'))}-{device}-{theme}";

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task The_screen_matches_its_baseline(string route, string device, string theme)
    {
        Assert.SkipUnless(BaselinesAreMaintainedHere,
            $"visual baselines are maintained for {string.Join("/", MaintainedBaselineDirectories)} only (ADR 0021); " +
            $"this run is on {CurrentOperatingSystem}. Run the suite on Linux, or in CI, to compare pixels.");

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
        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}{route}");
        await page.WaitForSelectorAsync(".gantt, .empty-state, .data-table, .glance-kpis, .form-grid", new() { Timeout = AppReady.BootTimeoutMilliseconds });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await AppReady.SettledAsync(page);

        var name = BaselineName(route, device, theme);
        var baselinePath = Path.Combine(BaselineDirectory, name + ".png");
        var current = await page.ScreenshotAsync(new() { FullPage = true, Animations = ScreenshotAnimations.Disabled, Caret = ScreenshotCaret.Hide });

        if (UpdatingBaselines)
        {
            Directory.CreateDirectory(BaselineDirectory);
            await File.WriteAllBytesAsync(baselinePath, current, Xunit.TestContext.Current.CancellationToken);
            Xunit.TestContext.Current.SendDiagnosticMessage($"visual baseline written: {baselinePath}");
            return;
        }

        if (!File.Exists(baselinePath))
        {
            var written = await WriteArtifactAsync(name + ".actual.png", current);
            Assert.Fail(
                $"{name}: no committed baseline at {baselinePath}. " +
                $"If this screen is new or was renamed, take {written} from the run artifacts, " +
                $"commit it as that baseline (or re-run with UPDATE_VISUAL_BASELINES=1 locally on Linux) — " +
                "a screen without a baseline is a screen nothing guards.");
        }

        var baseline = await File.ReadAllBytesAsync(baselinePath, Xunit.TestContext.Current.CancellationToken);
        var diff = await CompareAsync(page, baseline, current);

        if (diff.DifferentShare > AllowedDifferentPixelShare || diff.SizeDiffers)
        {
            // All three images travel together: a reviewer downloading the
            // artifact bundle can compare expected/actual/diff without also
            // having to fetch the baseline out of git.
            await WriteArtifactAsync(name + ".expected.png", baseline);
            await WriteArtifactAsync(name + ".actual.png", current);
            if (diff.DiffPng is not null)
                await WriteArtifactAsync(name + ".diff.png", diff.DiffPng);
        }

        Assert.False(diff.SizeDiffers, $"{name}: page size changed from {diff.BaselineSize} to {diff.CurrentSize} (set UPDATE_VISUAL_BASELINES=1 after an intended change)");
        Assert.True(diff.DifferentShare <= AllowedDifferentPixelShare,
            $"{name}: {diff.DifferentShare:P2} of pixels differ (allowed {AllowedDifferentPixelShare:P2}); see {name}.diff.png (set UPDATE_VISUAL_BASELINES=1 after an intended change)");
    }

    /// <summary>
    /// Every screen in <see cref="Screens"/> must have a committed baseline.
    /// The per-screen test above already fails on a missing one, but only for
    /// the screens it still knows about: this closes the case where a route is
    /// renamed and its old baseline is left behind, or a baseline is committed
    /// for a screen that no longer exists.
    /// </summary>
    [Fact]
    public void Every_screen_has_exactly_one_committed_baseline()
    {
        Assert.SkipUnless(BaselinesAreMaintainedHere,
            $"visual baselines are maintained for {string.Join("/", MaintainedBaselineDirectories)} only (ADR 0021); " +
            $"this run is on {CurrentOperatingSystem}.");

        var expected = Matrix
            .Select(s => BaselineName(s.Route, s.Device, s.Theme))
            .Order(StringComparer.Ordinal)
            .ToList();

        var committed = Directory.Exists(BaselineDirectory)
            ? Directory.EnumerateFiles(BaselineDirectory, "*.png")
                .Select(f => Path.GetFileNameWithoutExtension(f) ?? "")
                .Where(n => !n.EndsWith(".actual", StringComparison.Ordinal) && !n.EndsWith(".diff", StringComparison.Ordinal) && !n.EndsWith(".expected", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

        Assert.Equal(expected, committed);
    }

    private static bool UpdatingBaselines =>
        Environment.GetEnvironmentVariable("UPDATE_VISUAL_BASELINES") == "1"
        || Environment.GetEnvironmentVariable("VISUAL_UPDATE") == "1";

    /// <summary>
    /// The operating systems whose baselines are committed and compared. Adding
    /// one means adding a CI leg that runs on it — otherwise the new folder is
    /// exactly the dead weight this list exists to prevent.
    /// </summary>
    private static string[] MaintainedBaselineDirectories { get; } = ["linux"];

    private static string CurrentOperatingSystem =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    private static bool BaselinesAreMaintainedHere =>
        MaintainedBaselineDirectories.Contains(CurrentOperatingSystem) || UpdatingBaselines;

    private static string BaselineDirectory { get; } =
        Path.Combine(RepositoryRoot(), "tests", "WorkPlanStudio.E2E", "visual-baselines", CurrentOperatingSystem);

    private static async Task<string> WriteArtifactAsync(string fileName, byte[] content)
    {
        var artifacts = Environment.GetEnvironmentVariable("E2E_ARTIFACTS") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(artifacts);
        var path = Path.Combine(artifacts, fileName);
        await File.WriteAllBytesAsync(path, content, Xunit.TestContext.Current.CancellationToken);
        return path;
    }

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
