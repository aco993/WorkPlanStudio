using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Layout;
using WorkPlanStudio.Resources;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The navigation is grouped by what a visitor comes to do: planning first,
/// because the scheduler is the reason to be here, then the master data it
/// reads, and About last because it is about the project rather than the plant.
/// It used to be the order the pages were written in.
/// </summary>
public sealed class NavigationGroupsTests : AppBunitContext
{
    private IRenderedComponent<NavMenu> Rendered()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        return Render<NavMenu>();
    }

    private static string[] Hrefs(IEnumerable<AngleSharp.Dom.IElement> links) =>
        [.. links.Select(a => a.GetAttribute("href") ?? "")];

    [Fact]
    public void Links_come_in_the_order_a_visitor_needs_them()
    {
        var cut = Rendered();

        Assert.Equal(
            ["", "schedule", "production-orders", "working-time", "work-plans", "work-centers", "cost-centers", "import", "about"],
            Hrefs(cut.FindAll("a.nav-link")));
    }

    [Theory]
    [InlineData("nav-group-planning", "Nav_Group_Planning", new[] { "schedule", "production-orders", "working-time" })]
    [InlineData("nav-group-master-data", "Nav_Group_MasterData", new[] { "work-plans", "work-centers", "cost-centers", "import" })]
    public void Each_group_is_named_by_its_heading(string headingId, string key, string[] members)
    {
        var cut = Rendered();

        var group = cut.Find($"[role=group][aria-labelledby={headingId}]");
        Assert.Contains(key, cut.Find($"#{headingId}").TextContent, StringComparison.Ordinal);
        Assert.Equal(members, Hrefs(group.QuerySelectorAll("a.nav-link")));
    }

    [Fact]
    public void Every_icon_is_decorative()
    {
        var cut = Rendered();

        Assert.All(cut.FindAll("svg.nav-icon"), svg => Assert.Equal("true", svg.GetAttribute("aria-hidden")));
    }
}
