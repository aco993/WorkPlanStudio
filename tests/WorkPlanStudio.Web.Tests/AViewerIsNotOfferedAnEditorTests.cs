using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Measured on the published site as a guest: the list's row button said
/// <em>Edit</em>, it opened a page headed <em>Edit work plan</em> with thirty-six
/// dead fields, and <c>/work-plans/new</c> offered <em>New work plan</em> with six
/// dead fields and exactly one button — <em>Cancel</em>. Cancel what? Nothing had
/// been started, and nothing could be.
/// </summary>
public sealed class AViewerIsNotOfferedAnEditorTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    private BrowserDatabase Arrange(WorkspaceRole role, string name)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = _files.CreateDatabase(name, new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(role);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlanEditor>>(
            NullLogger<WorkPlanStudio.Pages.WorkPlanEditor>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlans>>(
            NullLogger<WorkPlanStudio.Pages.WorkPlans>.Instance);
        return database;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private async Task<IRenderedComponent<WorkPlanStudio.Pages.WorkPlanEditor>> EditorAsync(
        WorkspaceRole role, string name, int? id)
    {
        var database = Arrange(role, name);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, id));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".loading-row")));
        return cut;
    }

    [Fact]
    public async Task A_guest_opening_a_plan_is_not_told_it_is_editing_one()
    {
        var cut = await EditorAsync(WorkspaceRole.Guest, "guest-view.db", 1);

        Assert.Equal("Editor_ViewTitle", cut.Find("h1").TextContent.Trim());
    }

    [Fact]
    public async Task A_planner_opening_a_plan_still_reads_edit()
    {
        var cut = await EditorAsync(WorkspaceRole.Planner, "planner-edit.db", 1);

        Assert.Equal("Editor_EditTitle", cut.Find("h1").TextContent.Trim());
    }

    /// <summary>Cancel belongs to editing; for a reader it is a back-link wearing the word "discard".</summary>
    [Fact]
    public async Task A_guest_is_offered_no_cancel_because_nothing_was_started()
    {
        var cut = await EditorAsync(WorkspaceRole.Guest, "guest-cancel.db", 1);

        var labels = cut.FindAll(".head-actions button").Select(b => b.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Common_Cancel", labels);
        Assert.DoesNotContain("Common_Save", labels);
    }

    [Fact]
    public async Task A_planner_keeps_cancel_and_save()
    {
        var cut = await EditorAsync(WorkspaceRole.Planner, "planner-buttons.db", 1);

        var labels = cut.FindAll(".head-actions button").Select(b => b.TextContent.Trim()).ToList();
        Assert.Contains("Common_Cancel", labels);
        Assert.Contains("Common_Save", labels);
    }

    [Fact]
    public async Task A_guest_asking_for_a_new_plan_is_told_who_can_make_one()
    {
        var cut = await EditorAsync(WorkspaceRole.Guest, "guest-new.db", null);

        // No form at all — a field nobody on this page may submit is not read-only,
        // it is a promise that cannot be kept.
        Assert.Empty(cut.FindAll(".editor-grid input"));
        Assert.Contains("Editor_NewNeedsRole", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Editor_BackToPlans", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".head-actions button"));
    }

    [Fact]
    public async Task A_planner_asking_for_a_new_plan_gets_the_form()
    {
        var cut = await EditorAsync(WorkspaceRole.Planner, "planner-new.db", null);

        Assert.NotEmpty(cut.FindAll(".editor-grid input"));
        Assert.DoesNotContain("Editor_NewNeedsRole", cut.Markup, StringComparison.Ordinal);
    }

    private async Task<IRenderedComponent<WorkPlanStudio.Pages.WorkPlans>> ListAsync(
        WorkspaceRole role, string name)
    {
        var database = Arrange(role, name);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkPlans>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        return cut;
    }

    [Fact]
    public async Task The_row_button_says_where_it_goes()
    {
        var guest = await ListAsync(WorkspaceRole.Guest, "guest-list.db");

        Assert.All(
            guest.FindAll("tbody tr .actions-col button"),
            button => Assert.Equal("Common_View", button.GetAttribute("aria-label")));
    }

    [Fact]
    public async Task The_row_button_still_says_edit_for_a_planner()
    {
        var planner = await ListAsync(WorkspaceRole.Planner, "planner-list.db");

        Assert.Contains(
            planner.FindAll("tbody tr .actions-col button"),
            button => button.GetAttribute("aria-label") == "Common_Edit");
    }
}
