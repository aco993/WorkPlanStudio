using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Production orders are the one entity guaranteed to grow, and sorting them is
/// the affordance that makes a long list usable. It arrived with no test at all,
/// which is how its markup could be rewritten — from an inline-styled button to
/// a class — without anything noticing.
/// <para>
/// The assertions are about what a person and a screen reader get: the rows
/// change order, the header says which column carries the sort and in which
/// direction, and a second press reverses it.
/// </para>
/// </summary>
public sealed class OrderSortingTests : AppBunitContext
{
    private BrowserDatabase Arrange(TempDatabaseFiles files)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("orders-sort.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkspaceRole.Planner);
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        return database;
    }

    private static IReadOnlyList<string> OrderNumbers(IRenderedComponent<WorkPlanStudio.Pages.ProductionOrders> cut) =>
        [.. cut.FindAll("tbody tr td:first-child").Select(cell => cell.TextContent.Trim())];

    private static AngleSharp.Dom.IElement Header(IRenderedComponent<WorkPlanStudio.Pages.ProductionOrders> cut, string label) =>
        cut.FindAll("thead th").Single(th => th.QuerySelector(".th-sort")?.TextContent.Contains(label, StringComparison.Ordinal) == true);

    [Fact]
    public async Task Pressing_a_sortable_header_sorts_and_pressing_it_again_reverses()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.ProductionOrders>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        var header = Header(cut, "Orders_Number");
        Assert.Equal("none", header.GetAttribute("aria-sort"));

        await cut.InvokeAsync(() => Header(cut, "Orders_Number").QuerySelector(".th-sort")!.Click());
        cut.WaitForAssertion(() => Assert.Equal("ascending", Header(cut, "Orders_Number").GetAttribute("aria-sort")));
        var ascending = OrderNumbers(cut);
        Assert.Equal([.. ascending.Order(StringComparer.Ordinal)], ascending);

        await cut.InvokeAsync(() => Header(cut, "Orders_Number").QuerySelector(".th-sort")!.Click());
        cut.WaitForAssertion(() => Assert.Equal("descending", Header(cut, "Orders_Number").GetAttribute("aria-sort")));
        Assert.Equal([.. ascending.Reverse()], OrderNumbers(cut));
    }

    /// <summary>
    /// Only one column may claim the sort: two headers reporting a direction at
    /// once is a reader being told the table is sorted two ways.
    /// </summary>
    [Fact]
    public async Task Sorting_a_second_column_releases_the_first()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.ProductionOrders>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        await cut.InvokeAsync(() => Header(cut, "Orders_Number").QuerySelector(".th-sort")!.Click());
        await cut.InvokeAsync(() => Header(cut, "Field_Priority").QuerySelector(".th-sort")!.Click());

        cut.WaitForAssertion(() => Assert.Equal("ascending", Header(cut, "Field_Priority").GetAttribute("aria-sort")));
        Assert.Equal("none", Header(cut, "Orders_Number").GetAttribute("aria-sort"));
        Assert.Single(cut.FindAll("thead th[aria-sort]"), th => th.GetAttribute("aria-sort") != "none");
    }

    /// <summary>Every sortable header is a real button, so it is reachable by keyboard.</summary>
    [Fact]
    public async Task The_sortable_headers_are_buttons()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.ProductionOrders>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        var sortable = cut.FindAll("thead th .th-sort");
        Assert.Equal(3, sortable.Count);
        Assert.All(sortable, element =>
        {
            Assert.Equal("BUTTON", element.TagName);
            Assert.Equal("button", element.GetAttribute("type"));
            Assert.False(string.IsNullOrWhiteSpace(element.TextContent));
        });
    }
}
