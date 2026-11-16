using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Playwright;
using WorkPlanStudio.Prerender;

// Usage: WorkPlanStudio.Prerender <published wwwroot> [route ...]
//
// Routes are relative to the base href, without slashes: "" is the dashboard.
// The defaults are the three pages the Performance workflow measures and the
// README links to.
if (args.Length < 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: WorkPlanStudio.Prerender <published wwwroot> [route ...]");
    return 2;
}

var root = Path.GetFullPath(args[0]);
string[] routes = args.Length > 1 ? [.. args.Skip(1).Select(r => r.Trim('/'))] : ["", "schedule", "working-time"];
string[] cultures = ["en-US", "de-DE"];

await using var site = await StaticSite.StartAsync(root);
using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });

var snapshots = new List<Snapshot>();
foreach (var culture in cultures)
{
    foreach (var route in routes)
    {
        var html = await Capture.RenderAsync(browser, site.BaseUrl, route, culture);
        snapshots.Add(new Snapshot(route, culture, html));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"captured /{route} [{culture}]: {Encoding.UTF8.GetByteCount(html):N0} bytes"));
    }
}

var indexPath = Path.Combine(root, "index.html");
var original = await File.ReadAllTextAsync(indexPath);
var rewritten = IndexRewriter.Inject(original, snapshots, Capture.FirstPaintScript());
await File.WriteAllTextAsync(indexPath, rewritten);

// The publish wrote pre-compressed siblings of the old index.html. A host that
// honours them - the Performance workflow's server does - would serve the page
// without the picture, so they are written again from the new file.
await using (var source = File.OpenRead(indexPath))
await using (var gz = new System.IO.Compression.GZipStream(File.Create(indexPath + ".gz"), System.IO.Compression.CompressionLevel.SmallestSize))
    await source.CopyToAsync(gz);
await using (var source = File.OpenRead(indexPath))
await using (var br = new System.IO.Compression.BrotliStream(File.Create(indexPath + ".br"), System.IO.Compression.CompressionLevel.SmallestSize))
    await source.CopyToAsync(br);

Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"index.html: {Encoding.UTF8.GetByteCount(original):N0} -> {Encoding.UTF8.GetByteCount(rewritten):N0} bytes"));

var failures = await Verify.RunAsync(browser, site.BaseUrl, routes);
foreach (var failure in failures)
    Console.Error.WriteLine("FAIL: " + failure);
if (failures.Count > 0)
    return 1;

Console.WriteLine("verified: the picture paints first, hands over to the live page, and stays away from a returning visitor");
return 0;

namespace WorkPlanStudio.Prerender
{
    internal sealed record Snapshot(string Route, string Culture, string Html);

    /// <summary>
    /// The published folder over HTTP, with the SPA fallback GitHub Pages gives
    /// through 404.html. Kestrel rather than a Python one-liner so the tool
    /// serves .wasm and .dat with the types a browser accepts.
    /// </summary>
    internal sealed class StaticSite : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private StaticSite(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public static async Task<StaticSite> StartAsync(string root)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = root, WebRootPath = root });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            var files = new PhysicalFileProvider(root);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                ServeUnknownFileTypes = true,
                DefaultContentType = "application/octet-stream"
            });
            app.MapFallback(async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(Path.Combine(root, "index.html"));
            });

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
            return new StaticSite(app, address.TrimEnd('/') + "/");
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    internal static class Capture
    {
        /// <summary>
        /// The page is live and settled: Blazor has replaced the static boot
        /// screen and nothing on it is still loading. The same condition the
        /// first-paint script waits for before it hands over.
        /// </summary>
        public const string Settled =
            "() => { const app = document.getElementById('app');" +
            " return app && app.querySelector('.app-shell')" +
            " && !app.querySelector('.boot, .spinner, .loading-row, progress, [role=progressbar]'); }";

        public static string FirstPaintScript()
        {
            using var stream = typeof(Capture).Assembly.GetManifestResourceStream("first-paint.js")
                ?? throw new InvalidOperationException("first-paint.js is not embedded");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static async Task<string> RenderAsync(IBrowser browser, string baseUrl, string route, string culture)
        {
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1320, Height = 980 } });
            await context.AddInitScriptAsync($"window.localStorage.setItem('BlazorCulture', '{culture}');");
            var page = await context.NewPageAsync();

            await page.GotoAsync(baseUrl + route);
            await page.WaitForFunctionAsync(Settled, null, new() { Timeout = 180_000 });
            await page.EvaluateAsync("() => document.fonts.ready");
            await page.WaitForFunctionAsync(
                "() => document.getAnimations().every(a => a.playState === 'finished' || a.playState === 'idle')");

            // A picture, not a second copy of the application. Ids go because the
            // live page is about to carry the same ones; every attribute that
            // points at an id goes with them; Blazor's element-reference markers
            // and comments mean nothing outside Blazor.
            return await page.EvaluateAsync<string>("""
                () => {
                    const copy = document.getElementById('app').cloneNode(true);
                    const walker = document.createTreeWalker(copy, NodeFilter.SHOW_COMMENT);
                    const comments = [];
                    while (walker.nextNode()) comments.push(walker.currentNode);
                    comments.forEach(c => c.remove());
                    copy.querySelectorAll('script').forEach(s => s.remove());
                    const pointing = ['id', 'for', 'aria-labelledby', 'aria-describedby', 'aria-controls',
                                      'aria-owns', 'aria-activedescendant', 'aria-errormessage', 'tabindex', 'autofocus'];
                    for (const element of copy.querySelectorAll('*')) {
                        for (const name of pointing) element.removeAttribute(name);
                        for (const attribute of [...element.attributes])
                            if (attribute.name.startsWith('_bl_')) element.removeAttribute(attribute.name);
                    }
                    return copy.innerHTML;
                }
                """);
        }
    }

    internal static class IndexRewriter
    {
        public const string Marker = "<!-- prerendered by tools/WorkPlanStudio.Prerender; see docs/adr/0030 -->";

        private const string AppScript = "<script src=\"js/app.js\"></script>";
        private const string ErrorUi = "<div id=\"blazor-error-ui\">";

        public static string Inject(string index, IReadOnlyList<Snapshot> snapshots, string script)
        {
            if (index.Contains(Marker, StringComparison.Ordinal))
                throw new InvalidOperationException("index.html is already prerendered; publish again before running this.");
            if (!index.Contains(ErrorUi, StringComparison.Ordinal) || !index.Contains(AppScript, StringComparison.Ordinal))
                throw new InvalidOperationException("index.html no longer has the anchors this tool inserts beside.");

            var block = new StringBuilder();
            block.AppendLine(Marker);
            block.AppendLine("    <div id=\"prerendered\" hidden>");
            block.AppendLine("        <p class=\"prerender-status\" role=\"status\" aria-live=\"polite\" data-de=\"Die Anwendung wird gestartet …\">Starting the application …</p>");
            block.AppendLine("        <div class=\"prerender-snapshot\" inert></div>");
            block.AppendLine("    </div>");
            foreach (var snapshot in snapshots)
            {
                block.Append("    <template data-prerender-route=\"").Append(WebUtility.HtmlEncode(snapshot.Route))
                     .Append("\" data-prerender-culture=\"").Append(snapshot.Culture).Append("\">")
                     .Append(snapshot.Html)
                     .AppendLine("</template>");
            }

            var withBlock = index.Replace(ErrorUi, block + "    " + ErrorUi, StringComparison.Ordinal);
            return withBlock.Replace(AppScript, AppScript + "\n    <script>\n" + script + "    </script>", StringComparison.Ordinal);
        }
    }

    internal static class Verify
    {
        public static async Task<List<string>> RunAsync(IBrowser browser, string baseUrl, IReadOnlyList<string> routes)
        {
            var failures = new List<string>();
            foreach (var route in routes)
                await FirstVisitAsync(browser, baseUrl, route, "en-US", failures);
            await FirstVisitAsync(browser, baseUrl, routes[0], "de-DE", failures);
            await ReturningVisitAsync(browser, baseUrl, routes[0], failures);
            return failures;
        }

        private static async Task FirstVisitAsync(IBrowser browser, string baseUrl, string route, string culture, List<string> failures)
        {
            var where = $"/{route} [{culture}]";
            await using var context = await browser.NewContextAsync();
            await context.AddInitScriptAsync($"window.localStorage.setItem('BlazorCulture', '{culture}');");
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.Console += (_, message) => { if (message.Type == "error") errors.Add(message.Text); };
            page.PageError += (_, error) => errors.Add(error);

            await page.GotoAsync(baseUrl + route, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

            // Before the runtime: the picture is up and the boot screen is not.
            var early = await page.EvaluateAsync<bool>(
                "() => { const h = document.getElementById('prerendered'); const app = document.getElementById('app');" +
                " return !!h && !h.hidden && !!h.querySelector('.prerender-snapshot .app-shell') && app.hidden; }");
            if (!early)
                failures.Add($"{where}: the prerendered page was not showing at DOMContentLoaded");

            if (culture == "de-DE")
            {
                var german = await page.EvaluateAsync<bool>(
                    "() => (document.querySelector('#prerendered .prerender-snapshot')?.textContent ?? '').includes('Planung')");
                if (!german)
                    failures.Add($"{where}: the picture is not the German one");
            }

            // After the runtime: the picture is gone and the live page is visible.
            try
            {
                await page.WaitForFunctionAsync(
                    "() => !document.getElementById('prerendered') && !document.getElementById('app').hidden" +
                    " && document.querySelector('#app .app-shell')",
                    null, new() { Timeout = 180_000 });
            }
            catch (TimeoutException)
            {
                failures.Add($"{where}: the live page never took over from the picture");
            }

            if (await page.EvaluateAsync<int>("() => document.querySelectorAll('template[data-prerender-route]').length") != 0)
                failures.Add($"{where}: the templates were left in the page after the hand-over");
            failures.AddRange(errors.Select(e => $"{where}: console error: {e}"));
        }

        private static async Task ReturningVisitAsync(IBrowser browser, string baseUrl, string route, List<string> failures)
        {
            // A browser that already holds a database holds its own data. The value
            // only has to exist for the decision; the page is not left to boot on it.
            await using var context = await browser.NewContextAsync();
            await context.AddInitScriptAsync("window.localStorage.setItem('workplanstudio.db.version', '7');");
            var page = await context.NewPageAsync();
            await page.GotoAsync(baseUrl + route, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var shown = await page.EvaluateAsync<bool>("() => !!document.getElementById('prerendered')");
            if (shown)
                failures.Add($"/{route}: a returning visitor was shown the demo plant's picture");
        }
    }
}
