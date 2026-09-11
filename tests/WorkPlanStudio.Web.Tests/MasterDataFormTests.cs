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
/// The master-data forms, at the level a user meets them. Every one of these
/// reproduces something that previously made <b>Save</b> do nothing at all, or
/// saved a value the screen was not showing.
/// </summary>
public sealed class MasterDataFormTests : BunitContext
{
    private BrowserDatabase Arrange(TempDatabaseFiles files, WorkspaceRole role = WorkspaceRole.Planner)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("forms.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(role);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkCenters>>(NullLogger<WorkPlanStudio.Pages.WorkCenters>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.CostCenters>>(NullLogger<WorkPlanStudio.Pages.CostCenters>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlanEditor>>(NullLogger<WorkPlanStudio.Pages.WorkPlanEditor>.Instance);
        return database;
    }

    private static void Save(IRenderedComponent<WorkPlanStudio.Pages.WorkCenters> cut) =>
        cut.FindAll(".modal-foot .btn").First(button => !button.ClassList.Contains("btn-ghost")).Click();

    private IRenderedComponent<WorkPlanStudio.Pages.WorkCenters> OpenWorkCenterEditor(TempDatabaseFiles files)
    {
        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        cut.Find(".page-head .btn-primary").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        return cut;
    }

    [Fact]
    public async Task An_over_long_cost_centre_no_longer_makes_save_do_nothing()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = OpenWorkCenterEditor(files);
        cut.Find("#wc-code").Input("X1");
        cut.Find("#wc-name").Input("X");

        // The picker is the fix for the old free-text field: there is no way to
        // type a 25-character cost centre any more, and an id that does not exist
        // is reported rather than swallowed.
        cut.Find("#wc-cost-center").Change("9999");
        Save(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=alert]")));
        Assert.Contains("Val_CostCenterMissing", cut.Markup);
        var picker = cut.Find("#wc-cost-center");
        Assert.Equal("true", picker.GetAttribute("aria-invalid"));
        Assert.NotEmpty(cut.FindAll($"#{picker.GetAttribute("aria-describedby")}"));
    }

    [Fact]
    public async Task Refusing_to_deactivate_a_work_centre_in_use_is_now_visible()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        // CNC-300 runs operations on a released plan and a released order.
        var row = cut.FindAll("tbody tr").First(tr => tr.TextContent.Contains("CNC-300", StringComparison.Ordinal));
        row.QuerySelector("button.icon-btn")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));

        cut.Find("#wc-active").Change(false);
        Save(cut);

        // The README advertises this guard; before, the dialog simply sat there.
        cut.WaitForAssertion(() => Assert.Contains("Val_WorkCenterOrderUse", cut.Markup));
        Assert.NotEmpty(cut.FindAll("[role=alert]"));
        Assert.True((await new WorkCenterService(database).GetAllAsync(cancellationToken))
            .Single(center => center.Code == "CNC-300").IsActive);
    }

    [Fact]
    public async Task Clearing_a_numeric_field_does_not_silently_save_the_old_number()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        var row = cut.FindAll("tbody tr").First(tr => tr.TextContent.Contains("SAW-10", StringComparison.Ordinal));
        row.QuerySelector("button.icon-btn")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));

        // Selecting the value and pressing Backspace is what a user does. The box
        // is then visibly empty, and it used to save the number that was there.
        cut.Find("#wc-capacity").Input("");
        Save(cut);

        cut.WaitForAssertion(() => Assert.Contains("Val_Required", cut.Markup));
        var capacity = cut.Find("#wc-capacity");
        Assert.Equal("true", capacity.GetAttribute("aria-invalid"));
        Assert.Equal(1, (await new WorkCenterService(database).GetAllAsync(cancellationToken))
            .Single(center => center.Code == "SAW-10").ParallelCapacity);
    }

    [Fact]
    public async Task Every_message_a_failed_save_produces_reaches_the_summary()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = OpenWorkCenterEditor(files);
        Save(cut);   // empty form

        // The summary is the structural half of the fix: a message with no slot
        // of its own is listed here instead of being dropped on the floor.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=alert] li")));
        var listed = cut.FindAll("[role=alert] li").Select(item => item.TextContent.Trim()).ToArray();
        Assert.Equal(cut.FindAll(".field-error").Count, listed.Length);
        Assert.All(cut.FindAll("[role=alert] li a"), link =>
            Assert.NotEmpty(cut.FindAll(link.GetAttribute("href")!)));
    }

    [Fact]
    public async Task Deleting_a_work_plan_an_order_uses_shows_a_message_instead_of_crashing()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkPlans>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        var row = cut.FindAll("tbody tr").First(tr => tr.TextContent.Contains("WP-1001", StringComparison.Ordinal));
        row.QuerySelectorAll("button.icon-btn").Last().Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        cut.FindAll(".modal-foot .btn").First(button => button.ClassList.Contains("btn-danger")).Click();

        // The result used to be discarded entirely, and the unhandled
        // DbUpdateException behind it replaced the whole application.
        cut.WaitForAssertion(() => Assert.Contains("Val_WorkPlanInUse", cut.Markup));
        Assert.NotNull(await new WorkPlanService(database).GetAsync(
            (await new WorkPlanService(database).GetAllAsync(cancellationToken))
                .Single(plan => plan.PlanNumber == "WP-1001").Id,
            cancellationToken));
    }

    [Fact]
    public async Task A_cost_centre_can_be_created_from_its_own_page()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.CostCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        cut.Find(".page-head .btn-primary").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        cut.Find("#cc-code").Input("cc-4200");
        cut.Find("#cc-name").Input("Heat treatment");
        cut.FindAll(".modal-foot .btn").First(button => !button.ClassList.Contains("btn-ghost")).Click();

        cut.WaitForAssertion(() => Assert.Contains("CC-4200", cut.Markup));
        Assert.Contains(
            await new CostCenterService(database).GetAllAsync(cancellationToken),
            costCenter => costCenter.Code == "CC-4200" && costCenter.Name == "Heat treatment");
    }

    [Fact]
    public async Task A_guest_is_told_why_nothing_happened()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files, WorkspaceRole.Guest);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.CostCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        // The buttons are hidden for a Guest, so the service is the only route in
        // — and it must still say Forbidden rather than nothing.
        var result = await new CostCenterService(database, new RoleGuard(WorkspaceRole.Guest))
            .SaveAsync(new CostCenter { Code = "CC-X", Name = "Nope" }, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationResultStatus.Forbidden, result.Status);
        Assert.Contains("ReadOnly_Notice", cut.Markup);
    }
}
