using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// The recovery screen from ADR 0006 — the one screen a real user reaches when
/// something has gone wrong, and until now the least verified in the project:
/// the storage logic had thorough unit coverage, the UI in front of it had
/// none, in any browser or component test.
///
/// The payload is corrupted the same way the theme tests seed a preference, and
/// deliberately without knowing the current schema number: the app writes its
/// own version key on first boot, so a test that keeps that key and damages only
/// the data cannot go stale when the schema is bumped.
/// </summary>
public sealed class StorageRecoveryE2ETests : IClassFixture<PlaywrightFixture>
{
    private const string PayloadKey = "workplanstudio.db";
    private const string VersionKey = "workplanstudio.db.version";
    private const string CorruptPayload = "this is not base64 ***";

    private readonly PlaywrightFixture _fixture;

    public StorageRecoveryE2ETests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_corrupt_payload_is_preserved_and_reset_takes_two_steps()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await BootThenCorruptAsync(page);

        await page.GetByRole(AriaRole.Heading, new() { Name = "Browser data needs attention" })
            .WaitForAsync(new() { Timeout = AppReady.BootTimeoutMilliseconds });
        Assert.Contains("not valid Base64", await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);

        // The whole point of the screen: the damaged payload is still there to
        // be exported, not silently replaced by a fresh demo database.
        Assert.Equal(CorruptPayload, await page.EvaluateAsync<string>($"() => localStorage.getItem('{PayloadKey}')"));

        // Reset is deliberately two clicks, and the first one is not destructive.
        await page.GetByRole(AriaRole.Button, new() { Name = "Reset local demo database" }).ClickAsync();
        Assert.Equal(CorruptPayload, await page.EvaluateAsync<string>($"() => localStorage.getItem('{PayloadKey}')"));

        var confirm = page.GetByRole(AriaRole.Button, new() { Name = "Confirm reset and discard data" });
        await confirm.WaitForAsync();
        await confirm.ClickAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" })
            .WaitForAsync(new() { Timeout = AppReady.BootTimeoutMilliseconds });

        // "U1FMaXRl" is Base64 for "SQLite" — the replacement really is a database.
        var recovered = await page.EvaluateAsync<string>($"() => localStorage.getItem('{PayloadKey}')");
        Assert.StartsWith("U1FMaXRl", recovered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_damaged_payload_can_be_exported_before_it_is_discarded()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            AcceptDownloads = true
        });
        var page = await context.NewPageAsync();
        await BootThenCorruptAsync(page);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Browser data needs attention" })
            .WaitForAsync(new() { Timeout = AppReady.BootTimeoutMilliseconds });

        var download = await page.RunAndWaitForDownloadAsync(() =>
            page.GetByRole(AriaRole.Button, new() { Name = "Export stored payload" }).ClickAsync());

        Assert.StartsWith("workplanstudio-browser-database-v", download.SuggestedFilename, StringComparison.Ordinal);
        Assert.EndsWith(".json", download.SuggestedFilename, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payload_from_a_newer_schema_names_the_version_it_cannot_read()
    {
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}/");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" }).WaitForAsync();

        var version = int.Parse(
            await page.EvaluateAsync<string>($"() => localStorage.getItem('{VersionKey}')"),
            System.Globalization.CultureInfo.InvariantCulture);
        await page.EvaluateAsync($"v => localStorage.setItem('{VersionKey}', v)", (version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.ReloadAsync();

        await page.GetByRole(AriaRole.Heading, new() { Name = "Browser data needs attention" })
            .WaitForAsync(new() { Timeout = AppReady.BootTimeoutMilliseconds });
        Assert.Contains($"({version + 1})", await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_recovery_screen_has_no_wcag_aa_violations()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1320, Height = 980 }
        });
        var page = await context.NewPageAsync();
        await BootThenCorruptAsync(page);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Browser data needs attention" })
            .WaitForAsync(new() { Timeout = AppReady.BootTimeoutMilliseconds });
        await AppReady.SettledAsync(page);

        await AccessibilityE2ETests.AssertNoViolationsAsync(page, "storage recovery screen");
    }

    /// <summary>
    /// Boots the app once so it writes a valid payload and its own version key,
    /// then damages only the payload and reloads into the recovery screen.
    /// </summary>
    private async Task BootThenCorruptAsync(IPage page)
    {
        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}/");
        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" }).WaitForAsync();

        Assert.NotNull(await page.EvaluateAsync<string?>($"() => localStorage.getItem('{VersionKey}')"));
        await page.EvaluateAsync($"() => localStorage.setItem('{PayloadKey}', '{CorruptPayload}')");
        await page.ReloadAsync();
    }
}
