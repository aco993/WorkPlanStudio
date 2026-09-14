using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Four findings from an exploratory pass over the published site, all the same
/// shape: the app quietly throwing away what the user just did, or walking
/// through a one-way door without asking.
/// </summary>
public sealed class EditorsDoNotLoseWorkTests : AppBunitContext
{
    private BrowserDatabase Arrange(TempDatabaseFiles files, WorkspaceRole role = WorkspaceRole.Planner)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase($"editors.{role}.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(role);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlanEditor>>(NullLogger<WorkPlanStudio.Pages.WorkPlanEditor>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.ProductionOrders>>(NullLogger<WorkPlanStudio.Pages.ProductionOrders>.Instance);
        return database;
    }

    private async Task<IRenderedComponent<WorkPlanStudio.Pages.WorkPlanEditor>> OpenFirstPlanAsync(
        TempDatabaseFiles files, WorkspaceRole role = WorkspaceRole.Planner)
    {
        var database = Arrange(files, role);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var plans = new WorkPlanService(database, AllowAllGuard.Instance);
        var first = (await plans.GetAllAsync()).First();

        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, first.Id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wp-part")));
        return cut;
    }

    // ----- 1. an edit that was never saved is not thrown away in silence -----

    [Fact]
    public async Task Leaving_an_edited_work_plan_asks_before_the_edit_is_thrown_away()
    {
        using var files = new TempDatabaseFiles();
        var cut = await OpenFirstPlanAsync(files);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;

        await cut.ActAsync("#wp-part", part => part.Input("Edited and never saved"));
        await cut.InvokeAsync(() => navigation.NavigateTo("work-plans"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        Assert.Equal(before, navigation.Uri);
    }

    [Fact]
    public async Task Discarding_at_that_question_is_what_finally_leaves()
    {
        using var files = new TempDatabaseFiles();
        var cut = await OpenFirstPlanAsync(files);
        var navigation = Services.GetRequiredService<NavigationManager>();

        await cut.ActAsync("#wp-part", part => part.Input("Edited and never saved"));
        await cut.InvokeAsync(() => navigation.NavigateTo("work-plans"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));

        await cut.ActAsync(".modal-foot .btn-danger", discard => discard.Click());

        cut.WaitForAssertion(() => Assert.EndsWith("work-plans", navigation.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_untouched_work_plan_lets_you_leave_without_a_word()
    {
        using var files = new TempDatabaseFiles();
        var cut = await OpenFirstPlanAsync(files);
        var navigation = Services.GetRequiredService<NavigationManager>();

        await cut.InvokeAsync(() => navigation.NavigateTo("work-plans"));

        Assert.EndsWith("work-plans", navigation.Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".modal-card"));
    }

    [Fact]
    public async Task An_edit_that_was_typed_and_undone_is_not_an_edit()
    {
        // A flag set by an event handler would still say "unsaved". The page
        // compares what it would save against what it loaded, so putting the old
        // value back really does put the page back.
        using var files = new TempDatabaseFiles();
        var cut = await OpenFirstPlanAsync(files);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var original = cut.Find("#wp-part").GetAttribute("value");

        await cut.ActAsync("#wp-part", part => part.Input("something else"));
        await cut.ActAsync("#wp-part", part => part.Input(original ?? ""));
        await cut.InvokeAsync(() => navigation.NavigateTo("work-plans"));

        Assert.EndsWith("work-plans", navigation.Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".modal-card"));
    }

    // ----- 2. a persona that cannot save cannot type either -----

    [Theory]
    [InlineData(WorkspaceRole.Guest)]
    [InlineData(WorkspaceRole.Supervisor)]
    public async Task A_persona_without_the_policy_gets_a_form_it_cannot_type_into(WorkspaceRole role)
    {
        using var files = new TempDatabaseFiles();
        var cut = await OpenFirstPlanAsync(files, role);

        Assert.True(cut.Find("fieldset.form-fieldset").HasAttribute("disabled"),
            $"{role} can edit every field of a work plan it may never save");

        // The notice is the other half: the fields are dead, and the page says
        // who could bring them to life.
        Assert.NotEmpty(cut.FindAll(".readonly-notice"));
    }

    [Fact]
    public async Task A_planner_gets_the_same_form_alive()
    {
        using var files = new TempDatabaseFiles();
        var cut = await OpenFirstPlanAsync(files);

        Assert.False(cut.Find("fieldset.form-fieldset").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".readonly-notice"));
    }

    // ----- 3. the orders page asks before a one-way door, like every other page -----

    private async Task<IRenderedComponent<WorkPlanStudio.Pages.ProductionOrders>> OpenOrdersAsync(TempDatabaseFiles files)
    {
        var database = Arrange(files, WorkspaceRole.Supervisor);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var orders = new ProductionOrderService(database, AllowAllGuard.Instance);
        var plans = new WorkPlanService(database, AllowAllGuard.Instance);
        var plan = (await plans.GetAllAsync()).First(p => p.Status == WorkPlanStatus.Released);

        var draft = await orders.SaveAsync(new ProductionOrder
        {
            OrderNumber = "PO-DRAFT-1",
            WorkPlanId = plan.Id,
            Quantity = 10,
            Priority = 1,
            ReleaseLocal = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Unspecified),
            DueLocal = new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Unspecified)
        });
        Assert.True(draft.IsSuccess);

        var cut = Render<WorkPlanStudio.Pages.ProductionOrders>();
        cut.WaitForAssertion(() => Assert.Contains("PO-DRAFT-1", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public async Task Deleting_a_draft_order_asks_first_and_the_order_survives_a_no()
    {
        using var files = new TempDatabaseFiles();
        var cut = await OpenOrdersAsync(files);

        await cut.ActAsync(".actions-col .icon-btn.danger", delete => delete.Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        Assert.Contains("PO-DRAFT-1", cut.Markup, StringComparison.Ordinal);

        await cut.ActAsync(".modal-foot .btn-ghost", keep => keep.Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".modal-card")));
        Assert.Contains("PO-DRAFT-1", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirming_is_what_deletes_the_draft()
    {
        using var files = new TempDatabaseFiles();
        var cut = await OpenOrdersAsync(files);

        await cut.ActAsync(".actions-col .icon-btn.danger", delete => delete.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        await cut.ActAsync(".modal-foot .btn-danger", confirm => confirm.Click());

        cut.WaitForAssertion(() => Assert.DoesNotContain("PO-DRAFT-1", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Withdrawing_a_released_order_asks_too_because_it_cannot_be_released_again()
    {
        using var files = new TempDatabaseFiles();
        var cut = await OpenOrdersAsync(files);

        Assert.NotEmpty(cut.FindAll(".actions-col .btn-ghost"));
        await cut.ActAsync(".actions-col .btn-ghost", withdraw => withdraw.Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
    }

    // ----- 4. nothing tells a keyboard user to point at something -----

    [Fact]
    public void No_ui_string_asks_the_user_to_hover()
    {
        // The Gantt legend used to say "hover for the reason" while every segment
        // had carried that reason in an aria-label the whole time. The
        // information was never mouse-only; the sentence was.
        foreach (var culture in new[] { "", ".de" })
        {
            var resource = File.ReadAllText(
                Path.Combine(RepositoryRoot(), "src", "WorkPlanStudio", "Resources", $"SharedResource{culture}.resx"));

            foreach (var word in new[] { "hover", "Mauszeiger", "mouse over" })
                Assert.DoesNotContain(word, resource, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
