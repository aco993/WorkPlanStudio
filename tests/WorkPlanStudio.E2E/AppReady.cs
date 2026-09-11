using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// The two things every browser test needs and neither Playwright nor the app
/// provides out of the box: a navigation that survives one slow WebAssembly
/// boot, and a "the page has stopped changing" signal that is a condition
/// rather than a sleep.
/// </summary>
internal static class AppReady
{
    /// <summary>How long a first paint may take while the WASM runtime boots.</summary>
    public const int BootTimeoutMilliseconds = 60_000;

    /// <summary>
    /// "This route has finished drawing its own content", as one selector rather
    /// than two copies. It listed the shapes the routes happened to have when it
    /// was written, so adding a page whose body is a plain card — the account
    /// page — made the scan hang for a minute and then fail on a page that was
    /// perfectly healthy. `.card-body` is the general case and the others stay
    /// because they are the routes whose content arrives after their card does.
    /// </summary>
    public const string ContentSelector =
        ".gantt, .empty-state, .data-table, .glance-kpis, .about-grid, .form-grid, .param-grid, .card-body";

    /// <summary>
    /// Navigates and waits for <paramref name="readySelector"/>. The first
    /// navigation in a fresh context downloads and starts the WebAssembly
    /// runtime, which on a loaded shared runner occasionally overruns the
    /// timeout while the app itself is perfectly healthy.
    ///
    /// Exactly one reload is allowed, and only for that failure: a timeout
    /// waiting for the app shell to appear. Every other exception — and every
    /// assertion in every test — is left to fail on the first attempt, because a
    /// blanket retry converts a real defect into an intermittent one and hides
    /// it. A retry is reported as a diagnostic message so a run that needed one
    /// is visible rather than silently equal to a clean run.
    /// </summary>
    public static async Task GotoAsync(IPage page, string url, string readySelector = "main h1")
    {
        try
        {
            await page.GotoAsync(url);
            await page.WaitForSelectorAsync(readySelector, new() { Timeout = BootTimeoutMilliseconds });
            return;
        }
        catch (TimeoutException)
        {
            Xunit.TestContext.Current.SendDiagnosticMessage(
                $"app shell did not appear at {url} within {BootTimeoutMilliseconds} ms; reloading once");
        }

        await page.ReloadAsync();
        await page.WaitForSelectorAsync(readySelector, new() { Timeout = BootTimeoutMilliseconds });
    }

    /// <summary>
    /// Waits until the page has settled, on three conditions the app and the
    /// browser publish themselves:
    /// <list type="bullet">
    ///   <item>no spinner left in the DOM — every busy indicator in the app is a
    ///   <c>span.spinner</c>, so their absence is the app's own "not loading";</item>
    ///   <item><c>document.fonts.ready</c> resolved, awaited properly — a returned
    ///   promise is only awaited when it is the result of the evaluated
    ///   expression, which is why this is an arrow function and not a bare
    ///   <c>document.fonts.ready.then(...)</c>;</item>
    ///   <item>no CSS animation still running, which covers the modal's fade and
    ///   pop keyframes without knowing their duration.</item>
    /// </list>
    /// Waiting on conditions rather than a fixed sleep means a slow runner waits
    /// longer instead of producing a contrast violation or a pixel diff that
    /// reads like a regression.
    /// </summary>
    public static async Task SettledAsync(IPage page)
    {
        await page.WaitForFunctionAsync("() => document.querySelectorAll('.spinner').length === 0");
        await page.EvaluateAsync("() => document.fonts.ready");
        await page.WaitForFunctionAsync(
            "() => document.getAnimations().every(a => a.playState === 'finished' || a.playState === 'idle')");
    }
}
