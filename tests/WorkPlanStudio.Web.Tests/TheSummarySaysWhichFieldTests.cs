using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Measured on the published 0.3.9. Saving an empty cost centre produced this
/// summary:
/// <code>
/// &lt;li&gt;&lt;a href="#cc-code"&gt;Required&lt;/a&gt;&lt;/li&gt;
/// &lt;li&gt;&lt;a href="#cc-name"&gt;Required&lt;/a&gt;&lt;/li&gt;
/// </code>
/// Two links, both reading "Required", pointing at different controls — which is
/// WCAG 2.4.4, and is also simply useless: the summary exists to say what is
/// wrong <em>and where</em>. Three dialogs behaved the same way. The editor got
/// the prefix in 0.3.5; the dialogs never did.
/// </summary>
public sealed class TheSummarySaysWhichFieldTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    private BrowserDatabase Arrange()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = _files.CreateDatabase("summary.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkspaceRole.Planner);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.CostCenters>>(NullLogger<WorkPlanStudio.Pages.CostCenters>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkCenters>>(NullLogger<WorkPlanStudio.Pages.WorkCenters>.Instance);
        return database;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    /// <summary>Opens the "new" dialog on <typeparamref name="TPage"/> and saves it empty.</summary>
    private async Task<IRenderedComponent<TPage>> EmptySaveAsync<TPage>()
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        var database = Arrange();
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<TPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        await cut.ActAsync(".page-head .btn-primary", add => add.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        await cut.ActAsync(".modal-foot .btn-primary", save => save.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".error-summary-list li")));
        return cut;
    }

    private static List<string> Items<TPage>(IRenderedComponent<TPage> cut)
        where TPage : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll(".error-summary-list li").Select(item => item.TextContent.Trim()).ToList();

    [Fact]
    public async Task Two_required_fields_do_not_produce_two_identical_lines()
    {
        var cut = await EmptySaveAsync<WorkPlanStudio.Pages.CostCenters>();

        var items = Items(cut);
        Assert.True(items.Count >= 2, $"expected at least two messages, got {items.Count}");
        Assert.Equal(items.Count, items.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Each_line_names_its_field()
    {
        var cut = await EmptySaveAsync<WorkPlanStudio.Pages.CostCenters>();

        var items = Items(cut);
        Assert.Contains(items, item => item.StartsWith("Field_Code:", StringComparison.Ordinal));
        Assert.Contains(items, item => item.StartsWith("Field_Name:", StringComparison.Ordinal));
    }

    /// <summary>Link text is how a screen reader lists the links; two the same is WCAG 2.4.4.</summary>
    [Fact]
    public async Task No_two_links_in_the_summary_read_the_same()
    {
        var cut = await EmptySaveAsync<WorkPlanStudio.Pages.WorkCenters>();

        var links = cut.FindAll(".error-summary-list a").Select(a => a.TextContent.Trim()).ToList();
        Assert.NotEmpty(links);
        Assert.Equal(links.Count, links.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_link_still_points_at_the_control_it_names()
    {
        var cut = await EmptySaveAsync<WorkPlanStudio.Pages.CostCenters>();

        var code = cut.FindAll(".error-summary-list a")
            .Single(a => a.TextContent.StartsWith("Field_Code:", StringComparison.Ordinal));

        Assert.Equal("#cc-code", code.GetAttribute("href"));
    }

    /// <summary>
    /// A message that already names its own place must not be prefixed again:
    /// "Operations: Operation 10 · Op.: …" says it twice.
    /// </summary>
    [Fact]
    public void A_message_that_names_its_own_place_is_left_alone()
    {
        var editor = File.ReadAllText(
            Path.Join(RepoFiles.Root, "src", "WorkPlanStudio", "Pages", "WorkPlanEditor.razor"));

        Assert.Contains("_errors.Declare(\"Operations\", \"wp-operations\");", editor, StringComparison.Ordinal);
    }
}
