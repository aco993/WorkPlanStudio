using Microsoft.Playwright;

namespace WorkPlanStudio.E2E;

/// <summary>
/// The production-order lifecycle in a browser: raise a draft, release it, and
/// show that releasing froze the routing.
///
/// ADR 0011's central claim is that a released order stops depending on master
/// data — "editing a work plan does not change orders already released from
/// it". Until now that claim was covered by service-level tests only, and the
/// single browser assertion on this page checked that a button was visible. The
/// test below drives the whole claim through the UI: it changes the routing the
/// order was released from, proves the change landed, and then proves the
/// released order's schedule did not move.
/// </summary>
public sealed class ProductionOrderE2ETests : IClassFixture<PlaywrightFixture>
{
    private const string SourcePlan = "WP-1001";

    private readonly PlaywrightFixture _fixture;

    public ProductionOrderE2ETests(PlaywrightFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Releasing_an_order_freezes_the_routing_it_will_be_built_to()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1320, Height = 980 }
        });
        var page = await context.NewPageAsync();

        var orderNumber = await RaiseDraftOrderAsync(page);
        var order = page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = orderNumber });

        // A draft is not frozen and can still be released.
        Assert.Contains("Draft", await order.InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await order.GetByText("frozen").CountAsync());

        await order.GetByRole(AriaRole.Button, new() { Name = "Release" }).ClickAsync();

        await order.GetByText("frozen").WaitForAsync();
        Assert.Contains("Released", await order.InnerTextAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await order.GetByRole(AriaRole.Button, new() { Name = "Release" }).CountAsync());

        // The released order is now schedulable, and this is where it lands.
        var schedule = new SchedulePage(page, _fixture.BaseUrl);
        await schedule.GotoAsync();
        var job = page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = orderNumber });
        await job.WaitForAsync();
        var scheduledBefore = await job.InnerTextAsync();
        var makespanBefore = await schedule.MakespanTextAsync();

        // Change the routing the order was released from, and prove the change
        // really landed — otherwise "nothing moved" would pass for the wrong reason.
        var planSummaryBefore = await PlanRowTextAsync(page);
        await RaiseRunTimeOfFirstOperationAsync(page);
        var planSummaryAfter = await PlanRowTextAsync(page);
        Assert.NotEqual(planSummaryBefore, planSummaryAfter);

        // The order carries its own copy of the routing, so its schedule is unchanged.
        await schedule.GotoAsync();
        await job.WaitForAsync();
        Assert.Equal(scheduledBefore, await job.InnerTextAsync());
        Assert.Equal(makespanBefore, await schedule.MakespanTextAsync());
    }

    [Fact]
    public async Task A_cancelled_order_leaves_the_schedule()
    {
        await using var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1320, Height = 980 }
        });
        var page = await context.NewPageAsync();

        var orderNumber = await RaiseDraftOrderAsync(page);
        var order = page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = orderNumber });
        await order.GetByRole(AriaRole.Button, new() { Name = "Release" }).ClickAsync();
        await order.GetByText("frozen").WaitForAsync();

        var schedule = new SchedulePage(page, _fixture.BaseUrl);
        await schedule.GotoAsync();
        Assert.Equal(1, await page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = orderNumber }).CountAsync());

        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}/production-orders");
        await order.GetByRole(AriaRole.Button, new() { Name = "Cancel order" }).ClickAsync();
        await page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = orderNumber })
            .GetByText("Cancelled").WaitForAsync();

        await schedule.GotoAsync();
        Assert.Equal(0, await page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = orderNumber }).CountAsync());
    }

    /// <summary>
    /// Creates a draft order against <see cref="SourcePlan"/> inside the June
    /// 2026 horizon the seed data uses, and returns its order number.
    /// </summary>
    private async Task<string> RaiseDraftOrderAsync(IPage page)
    {
        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}/production-orders");
        await page.GetByRole(AriaRole.Button, new() { Name = "New order" }).ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "New production order" });
        await dialog.WaitForAsync();

        var orderNumber = await dialog.GetByLabel("Order no.").InputValueAsync();
        Assert.StartsWith("PO-", orderNumber, StringComparison.Ordinal);

        var routing = dialog.GetByLabel("Routing");
        var wanted = routing.Locator("option").Filter(new LocatorFilterOptions { HasText = SourcePlan });
        var value = await wanted.GetAttributeAsync("value");
        Assert.NotNull(value);
        await routing.SelectOptionAsync(new SelectOptionValue { Value = value });

        await dialog.GetByLabel("Quantity").FillAsync("5");
        await dialog.GetByLabel("Release", new() { Exact = true }).FillAsync("2026-06-01");
        await dialog.GetByLabel("Due", new() { Exact = true }).FillAsync("2026-06-12");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = orderNumber }).WaitForAsync();
        return orderNumber;
    }

    /// <summary>The work-plan list row for the source plan, which prints its computed time and cost.</summary>
    private async Task<string> PlanRowTextAsync(IPage page)
    {
        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}/work-plans");
        var row = page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = SourcePlan });
        await row.WaitForAsync();
        return await row.InnerTextAsync();
    }

    /// <summary>
    /// Doubles the per-piece time of the plan's first operation. The cell is
    /// found through its column header rather than by a fixed index, so adding
    /// or reordering a column moves the test with the table instead of silently
    /// editing the wrong number. The comparison ignores case because the header
    /// is upper-cased by CSS and read back rendered.
    /// </summary>
    private async Task RaiseRunTimeOfFirstOperationAsync(IPage page)
    {
        await AppReady.GotoAsync(page, $"{_fixture.BaseUrl}/work-plans");
        await page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = SourcePlan })
            .GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Edit work plan" }).WaitForAsync();

        var headers = await page.Locator("table.op-table thead th").AllInnerTextsAsync();
        int column = headers.ToList().FindIndex(h => h.Contains("Per piece", StringComparison.OrdinalIgnoreCase));
        Assert.True(column >= 0, $"no 'Per piece' column among [{string.Join(", ", headers)}]");

        var cell = page.Locator("table.op-table tbody tr").First.Locator("td").Nth(column).Locator("input");
        var current = decimal.Parse(await cell.InputValueAsync(), System.Globalization.CultureInfo.InvariantCulture);
        await cell.FillAsync((current * 4 + 5).ToString(System.Globalization.CultureInfo.InvariantCulture));

        await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await page.WaitForURLAsync("**/work-plans");
    }
}
