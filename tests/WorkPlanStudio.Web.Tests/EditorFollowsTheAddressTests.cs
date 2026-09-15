using Bunit;
using Microsoft.AspNetCore.Components;
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
/// The router keeps one instance of the editor alive while only the route
/// parameter changes, so every test here re-parameterises the component it
/// already rendered instead of rendering a new one. That is the whole point: a
/// suite that only ever renders fresh components cannot see a page that fails to
/// notice it was asked for something else, and this one did not — for two
/// releases, on the app's central form.
/// </summary>
public sealed class EditorFollowsTheAddressTests : AppBunitContext
{
    private BrowserDatabase Arrange(TempDatabaseFiles files)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("address.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkspaceRole.Planner);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlanEditor>>(NullLogger<WorkPlanStudio.Pages.WorkPlanEditor>.Instance);
        return database;
    }

    /// <summary>Two plans that are easy to tell apart, in a ready database.</summary>
    private async Task<(BrowserDatabase Database, int FirstId, int SecondId, string FirstNumber, string SecondNumber)>
        TwoPlansAsync(TempDatabaseFiles files)
    {
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var plans = await new WorkPlanService(database).GetAllAsync(Xunit.TestContext.Current.CancellationToken);
        var first = plans.First();
        var second = plans.Skip(1).First();
        Assert.NotEqual(first.PlanNumber, second.PlanNumber);
        return (database, first.Id, second.Id, first.PlanNumber, second.PlanNumber);
    }

    private IRenderedComponent<WorkPlanStudio.Pages.WorkPlanEditor> OpenPlan(int id)
    {
        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wp-number")));
        return cut;
    }

    /// <summary>
    /// Navigates the open editor to another plan the way the router does: the same
    /// component instance, re-rendered with a new route parameter.
    /// </summary>
    private static void GoTo(IRenderedComponent<WorkPlanStudio.Pages.WorkPlanEditor> cut, int? id) =>
        cut.Render(p => p.Add(e => e.Id, id));

    // ----- the address is the question; the form has to answer it -----

    [Fact]
    public async Task An_editor_asked_for_another_plan_shows_that_plan()
    {
        using var files = new TempDatabaseFiles();
        var (_, firstId, secondId, firstNumber, secondNumber) = await TwoPlansAsync(files);

        var cut = OpenPlan(firstId);
        Assert.Equal(firstNumber, cut.Find("#wp-number").GetAttribute("value"));

        GoTo(cut, secondId);

        cut.WaitForAssertion(() => Assert.Equal(secondNumber, cut.Find("#wp-number").GetAttribute("value")));
    }

    [Fact]
    public async Task An_edit_made_after_a_route_change_reaches_the_plan_the_address_names()
    {
        // The measured failure, and the reason this is the finding that mattered:
        // the page kept the first plan's form under the second plan's address, so
        // a save went to the plan the user had just navigated away from while the
        // one they asked for was left untouched. Neither of them is what anybody
        // asked for; both of them are silent.
        using var files = new TempDatabaseFiles();
        var (database, firstId, secondId, _, secondNumber) = await TwoPlansAsync(files);
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var plans = new WorkPlanService(database);
        var firstNameBefore = (await plans.GetAsync(firstId, cancellationToken))!.PartName;

        var cut = OpenPlan(firstId);
        GoTo(cut, secondId);
        cut.WaitForAssertion(() => Assert.Equal(secondNumber, cut.Find("#wp-number").GetAttribute("value")));

        await cut.ActAsync("#wp-part", part => part.Input("Edited after the address changed"));
        await cut.ActAsync(".page-head .btn-primary", save => save.Click());

        cut.WaitForAssertion(async () =>
            Assert.Equal("Edited after the address changed", (await plans.GetAsync(secondId, cancellationToken))!.PartName));
        Assert.Equal(firstNameBefore, (await plans.GetAsync(firstId, cancellationToken))!.PartName);
    }

    // ----- the two states v0.3.4 added must not outlive the plan they belong to -----

    [Fact]
    public async Task A_plan_that_is_missing_does_not_make_the_next_one_look_deleted()
    {
        using var files = new TempDatabaseFiles();
        var (_, firstId, _, firstNumber, _) = await TwoPlansAsync(files);

        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, 9999));
        cut.WaitForAssertion(() => Assert.Contains("Editor_PlanMissing", cut.Markup, StringComparison.Ordinal));

        GoTo(cut, firstId);

        cut.WaitForAssertion(() => Assert.Equal(firstNumber, cut.Find("#wp-number").GetAttribute("value")));
        Assert.DoesNotContain("Editor_PlanMissing", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_that_is_missing_does_not_take_the_new_plan_form_with_it()
    {
        using var files = new TempDatabaseFiles();
        _ = await TwoPlansAsync(files);

        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, 9999));
        cut.WaitForAssertion(() => Assert.Contains("Editor_PlanMissing", cut.Markup, StringComparison.Ordinal));

        GoTo(cut, null);   // "+ New work plan"

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#wp-number")));
        Assert.DoesNotContain("Editor_PlanMissing", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Editor_NewTitle", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_plan_followed_by_a_missing_one_says_the_missing_one_is_missing()
    {
        // The same leak facing the other way: a full editor, with Save, for a plan
        // that is not there.
        using var files = new TempDatabaseFiles();
        var (_, firstId, _, _, _) = await TwoPlansAsync(files);

        var cut = OpenPlan(firstId);
        GoTo(cut, 9999);

        cut.WaitForAssertion(() => Assert.Contains("Editor_PlanMissing", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("#wp-number"));
        Assert.DoesNotContain("Common_Save", cut.Markup, StringComparison.Ordinal);
    }

    // ----- a page with no form has nothing to lose -----

    [Fact]
    public async Task The_missing_plan_page_does_not_claim_unsaved_changes()
    {
        using var files = new TempDatabaseFiles();
        _ = await TwoPlansAsync(files);
        var navigation = Services.GetRequiredService<NavigationManager>();

        var cut = Render<WorkPlanStudio.Pages.WorkPlanEditor>(p => p.Add(e => e.Id, 9999));
        cut.WaitForAssertion(() => Assert.Contains("Editor_PlanMissing", cut.Markup, StringComparison.Ordinal));

        await cut.InvokeAsync(() => navigation.NavigateTo("work-plans"));

        Assert.EndsWith("work-plans", navigation.Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".modal-card"));
    }

    // ----- and the guard on the fix, which is the half that is easy to get wrong -----

    [Fact]
    public async Task A_parameter_set_that_is_not_a_route_change_does_not_reload_over_typing()
    {
        // OnParametersSetAsync also runs when a cascading value changes - switching
        // persona is one - so reloading on every call would throw away live typing
        // and trade a rare wrong-plan write for a common lost-work one.
        using var files = new TempDatabaseFiles();
        var (_, firstId, _, _, _) = await TwoPlansAsync(files);

        var cut = OpenPlan(firstId);
        await cut.ActAsync("#wp-part", part => part.Input("Typed, not yet saved"));

        GoTo(cut, firstId);   // same id: a re-render, not a navigation

        cut.WaitForAssertion(() =>
            Assert.Equal("Typed, not yet saved", cut.Find("#wp-part").GetAttribute("value")));
    }
}
