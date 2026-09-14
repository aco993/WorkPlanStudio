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
public sealed class MasterDataFormTests : AppBunitContext
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

    private static Task SaveAsync(IRenderedComponent<WorkPlanStudio.Pages.WorkCenters> cut) =>
        cut.ActAsync(".modal-foot .btn", button => !button.ClassList.Contains("btn-ghost"), save => save.Click());

    private async Task<IRenderedComponent<WorkPlanStudio.Pages.WorkCenters>> OpenWorkCenterEditorAsync(TempDatabaseFiles files)
    {
        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        await cut.ActAsync(".page-head .btn-primary", newCenter => newCenter.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        return cut;
    }

    [Fact]
    public async Task An_over_long_cost_centre_no_longer_makes_save_do_nothing()
    {
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = await OpenWorkCenterEditorAsync(files);
        await cut.ActAsync("#wc-code", code => code.Input("X1"));
        await cut.ActAsync("#wc-name", name => name.Input("X"));

        // The picker is the fix for the old free-text field: there is no way to
        // type a 25-character cost centre any more, and an id that does not exist
        // is reported rather than swallowed.
        await cut.ActAsync("#wc-cost-center", picker => picker.Change("9999"));
        await SaveAsync(cut);

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
        await cut.ActAsync(
            "tbody tr",
            tr => tr.TextContent.Contains("CNC-300", StringComparison.Ordinal),
            row => row.QuerySelector("button.icon-btn")!.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));

        await cut.ActAsync("#wc-active", active => active.Change(false));
        await SaveAsync(cut);

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
        await cut.ActAsync(
            "tbody tr",
            tr => tr.TextContent.Contains("SAW-10", StringComparison.Ordinal),
            row => row.QuerySelector("button.icon-btn")!.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));

        // Selecting the value and pressing Backspace is what a user does. The box
        // is then visibly empty, and it used to save the number that was there.
        await cut.ActAsync("#wc-capacity", capacity => capacity.Input(""));
        await SaveAsync(cut);

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

        var cut = await OpenWorkCenterEditorAsync(files);
        await SaveAsync(cut);   // empty form

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

        // The refusal is now stated before the click rather than after the
        // confirmation: the button is disabled and says why, as the work-centre
        // and cost-centre lists have always done. (The service still refuses -
        // see the assertion below, and ProductionOrderService's own tests.)
        var row = cut.FindAll("tbody tr").Single(tr => tr.TextContent.Contains("WP-1001", StringComparison.Ordinal));
        var delete = row.QuerySelectorAll("button.icon-btn").Last();
        Assert.True(delete.HasAttribute("disabled"));
        Assert.Equal("WorkPlans_InUse", delete.GetAttribute("title"));

        var service = new WorkPlanService(database);
        var plan = (await service.GetAllAsync(cancellationToken)).Single(p => p.PlanNumber == "WP-1001");
        var refused = await service.DeleteAsync(plan.Id, cancellationToken);

        // The result used to be discarded entirely, and the unhandled
        // DbUpdateException behind it replaced the whole application.
        Assert.Equal(ApplicationResultStatus.Conflict, refused.Status);
        Assert.Contains(refused.ValidationIssues!, issue => issue.MessageKey == "Val_WorkPlanInUse");
        Assert.NotNull(await service.GetAsync(plan.Id, cancellationToken));
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

        await cut.ActAsync(".page-head .btn-primary", newCostCenter => newCostCenter.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        await cut.ActAsync("#cc-code", code => code.Input("cc-4200"));
        await cut.ActAsync("#cc-name", name => name.Input("Heat treatment"));
        await cut.ActAsync(
            ".modal-foot .btn", button => !button.ClassList.Contains("btn-ghost"), save => save.Click());

        // The dialog closes only after the save succeeded and the page reloaded
        // from the database, so waiting on that is waiting on the write. Waiting
        // on the code appearing in the markup is not the same thing — a rejected
        // save puts the code in an error banner, which is how this read as a
        // flake on a slower runner.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".modal-card")));
        Assert.Empty(cut.FindAll(".form-banner.error"));
        Assert.Contains("CC-4200", cut.Markup);

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
