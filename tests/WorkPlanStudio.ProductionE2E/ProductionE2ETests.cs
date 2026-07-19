using Microsoft.Playwright;

namespace WorkPlanStudio.ProductionE2E;

public sealed class ProductionE2ETests
{
    public static bool ProductionEnvironmentAvailable =>
        Required("E2E_PRODUCTION_BASE_URL") is not null &&
        Required("E2E_PRODUCTION_EMAIL") is not null &&
        Required("E2E_PRODUCTION_PASSWORD") is not null;

    [Fact(Skip = "Production URL and bootstrap credentials are required.", SkipUnless = nameof(ProductionEnvironmentAvailable))]
    public async Task Login_form_exposes_accessible_names_and_secure_autocomplete_hints()
    {
        var (playwright, browser, page) = await OpenAsync();
        try
        {
            await page.GotoAsync(BaseUrl);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })
                .WaitForAsync(new() { Timeout = 60_000 });

            var email = page.GetByLabel("Email");
            var password = page.GetByLabel("Password");
            Assert.Equal("email", await email.GetAttributeAsync("type"));
            Assert.Equal("username", await email.GetAttributeAsync("autocomplete"));
            Assert.Equal("password", await password.GetAttributeAsync("type"));
            Assert.Equal("current-password", await password.GetAttributeAsync("autocomplete"));
            Assert.Equal("auth-title", await page.Locator("main section").GetAttributeAsync("aria-labelledby"));
            Assert.Equal("en", await page.Locator("html").GetAttributeAsync("lang"));
            Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).IsEnabledAsync());
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }

    [Fact(Skip = "Production URL and bootstrap credentials are required.", SkipUnless = nameof(ProductionEnvironmentAvailable))]
    public async Task Bootstrap_administrator_can_reach_server_dashboard_and_security_page()
    {
        var (playwright, browser, page) = await OpenAsync();
        try
        {
            await LoginAsync(page);

            Assert.Equal("Server", (await page.Locator(".topbar .pill").InnerTextAsync()).Trim());
            Assert.True(await page.GetByText(Email, new() { Exact = true }).IsVisibleAsync());
            Assert.True(await page.GetByRole(AriaRole.Navigation, new() { Name = "Primary navigation" }).IsVisibleAsync());
            Assert.Equal("#main-content", await page.GetByText("Skip to main content", new() { Exact = true })
                .GetAttributeAsync("href"));
            Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).IsVisibleAsync());

            await page.GetByRole(AriaRole.Link, new() { Name = "Account security" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Account security" })
                .WaitForAsync(new() { Timeout = 30_000 });
            Assert.True(await page.GetByRole(AriaRole.Heading, new() { Name = "Multi-factor authentication" })
                .IsVisibleAsync());
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }

    [Fact(Skip = "Production URL and bootstrap credentials are required.", SkipUnless = nameof(ProductionEnvironmentAvailable))]
    public async Task Authenticated_server_UI_preserves_language_and_mobile_navigation_semantics()
    {
        var (playwright, browser, page) = await OpenAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }
        });
        try
        {
            await LoginAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "DE" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Übersicht" })
                .WaitForAsync(new() { Timeout = 30_000 });
            Assert.Equal("de", await page.Locator("html").GetAttributeAsync("lang"));

            var menu = page.GetByRole(AriaRole.Button, new() { Name = "Menu" });
            await menu.ClickAsync();
            Assert.True(await page.GetByRole(AriaRole.Navigation, new() { Name = "Hauptnavigation" }).IsVisibleAsync());
            await page.GetByRole(AriaRole.Link, new() { Name = "Kontosicherheit" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Kontosicherheit" })
                .WaitForAsync(new() { Timeout = 30_000 });
        }
        finally
        {
            await browser.CloseAsync();
            playwright.Dispose();
        }
    }

    private static async Task LoginAsync(IPage page)
    {
        await page.GotoAsync(BaseUrl);
        await page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })
            .WaitForAsync(new() { Timeout = 60_000 });
        await page.GetByLabel("Email").FillAsync(Email);
        await page.GetByLabel("Password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Dashboard" })
            .WaitForAsync(new() { Timeout = 60_000 });
    }

    private static async Task<(IPlaywright Playwright, IBrowser Browser, IPage Page)> OpenAsync(
        BrowserNewPageOptions? pageOptions = null)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        return (playwright, browser, await browser.NewPageAsync(pageOptions));
    }

    private static string BaseUrl => Required("E2E_PRODUCTION_BASE_URL")!.TrimEnd('/');
    private static string Email => Required("E2E_PRODUCTION_EMAIL")!;
    private static string Password => Required("E2E_PRODUCTION_PASSWORD")!;

    private static string? Required(string name) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? null
            : Environment.GetEnvironmentVariable(name);
}
