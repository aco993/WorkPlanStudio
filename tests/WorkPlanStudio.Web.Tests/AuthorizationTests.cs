using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkPlanStudio.Components;
using WorkPlanStudio.Data;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The persona model, end to end: the policy table through the real
/// <see cref="IAuthorizationService"/>, the persona provider, the guard that
/// stops writes at the service boundary, and the pages that hide what a
/// persona may not do.
/// </summary>
public sealed class AuthorizationTests : BunitContext
{
    // ----- the policy table through the real pipeline -----

    [Theory]
    [InlineData(WorkspaceRole.Planner, Permissions.ManageMasterData, true)]
    [InlineData(WorkspaceRole.Planner, Permissions.ManageOrders, true)]
    [InlineData(WorkspaceRole.Planner, Permissions.ManagePlantRules, true)]
    [InlineData(WorkspaceRole.Planner, Permissions.ManageAbsences, true)]
    [InlineData(WorkspaceRole.Planner, Permissions.ResetData, true)]
    [InlineData(WorkspaceRole.Supervisor, Permissions.ManageMasterData, false)]
    [InlineData(WorkspaceRole.Supervisor, Permissions.ManageOrders, true)]
    [InlineData(WorkspaceRole.Supervisor, Permissions.ManagePlantRules, false)]
    [InlineData(WorkspaceRole.Supervisor, Permissions.ManageAbsences, true)]
    [InlineData(WorkspaceRole.Supervisor, Permissions.ResetData, false)]
    [InlineData(WorkspaceRole.Guest, Permissions.ManageMasterData, false)]
    [InlineData(WorkspaceRole.Guest, Permissions.ManageOrders, false)]
    [InlineData(WorkspaceRole.Guest, Permissions.ManagePlantRules, false)]
    [InlineData(WorkspaceRole.Guest, Permissions.ManageAbsences, false)]
    [InlineData(WorkspaceRole.Guest, Permissions.ResetData, false)]
    public async Task The_pipeline_grants_exactly_what_the_table_says(WorkspaceRole role, string policy, bool expected)
    {
        var provider = Services.AddDemoAuthorization(role);
        var authorization = Services.GetRequiredService<IAuthorizationService>();

        var state = await provider.GetAuthenticationStateAsync();
        var result = await authorization.AuthorizeAsync(state.User, resource: null, policy);

        Assert.Equal(expected, result.Succeeded);
        Assert.Equal(expected, Permissions.Grants(role, policy));   // the table agrees with the pipeline
        Assert.Equal(expected, await Services.GetRequiredService<IPermissionGuard>().CanAsync(policy, Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_policy_is_registered()
    {
        Services.AddDemoAuthorization(WorkspaceRole.Guest);
        var policies = Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var policy in Permissions.All)
            Assert.NotNull(await policies.GetPolicyAsync(policy));
    }

    // ----- the provider -----

    [Fact]
    public async Task A_first_visit_is_the_planner_and_switching_is_remembered_and_announced()
    {
        var store = new FakePersonaStore();
        var provider = new DemoAuthenticationStateProvider(store);
        var announced = 0;
        provider.AuthenticationStateChanged += _ => announced++;

        var state = await provider.GetAuthenticationStateAsync();
        Assert.Equal(WorkspaceRole.Planner, provider.CurrentRole);
        Assert.True(state.User.IsInRole("Planner"));
        Assert.Equal(DemoAuthenticationStateProvider.AuthenticationType, state.User.Identity!.AuthenticationType);

        await provider.SwitchAsync(WorkspaceRole.Guest, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceRole.Guest, store.Stored);
        Assert.Equal(1, announced);
        Assert.True((await provider.GetAuthenticationStateAsync()).User.IsInRole("Guest"));
    }

    [Fact]
    public async Task A_stored_persona_survives_a_new_session()
    {
        var provider = new DemoAuthenticationStateProvider(new FakePersonaStore { Stored = WorkspaceRole.Supervisor });

        Assert.True((await provider.GetAuthenticationStateAsync()).User.IsInRole("Supervisor"));
    }

    // ----- the service boundary -----

    [Fact]
    public async Task A_guest_cannot_write_even_when_calling_the_service_directly()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("guest.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var guest = new RoleGuard(WorkspaceRole.Guest);

        var centers = new WorkCenterService(database, guest);
        var plans = new WorkPlanService(database, guest);
        var orders = new ProductionOrderService(database, guest);
        var settings = new PlantSettingsService(database, guest);
        var center = (await centers.GetAllAsync(cancellationToken)).First();

        Assert.Equal(ApplicationResultStatus.Forbidden, (await centers.SaveAsync(new WorkCenter { Code = "X", Name = "X" }, cancellationToken)).Status);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await centers.DeleteAsync(center.Id, cancellationToken)).Status);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await centers.AddAbsenceAsync(new WorkCenterAbsence { WorkCenterId = center.Id, Start = DateTime.Today, End = DateTime.Today.AddDays(1) }, cancellationToken)).Status);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await plans.CreateAsync(new WorkPlan(), cancellationToken)).Status);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await plans.DeleteAsync(1, cancellationToken)).Status);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await orders.SaveAsync(new ProductionOrder(), cancellationToken)).Status);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await orders.ReleaseAsync(1, cancellationToken)).Status);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await orders.CancelAsync(1, cancellationToken)).Status);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await settings.SaveAsync(new PlantSettings(), cancellationToken)).Status);

        // …and nothing was written.
        Assert.Single(await centers.GetAbsencesAsync(cancellationToken));
        Assert.Equal("NW", (await settings.GetAsync(cancellationToken)).State);
    }

    [Fact]
    public async Task A_supervisor_can_release_orders_but_not_touch_master_data()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("supervisor.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        var supervisor = new RoleGuard(WorkspaceRole.Supervisor);

        var orders = new ProductionOrderService(database, supervisor);
        var released = (await orders.GetAllAsync(cancellationToken)).First(o => o.Status == ProductionOrderStatus.Released);
        Assert.True((await orders.CancelAsync(released.Id, cancellationToken)).IsSuccess);

        var centers = new WorkCenterService(database, supervisor);
        Assert.Equal(ApplicationResultStatus.Forbidden, (await centers.SaveAsync(new WorkCenter { Code = "X", Name = "X" }, cancellationToken)).Status);
        Assert.True((await centers.AddAbsenceAsync(new WorkCenterAbsence
        {
            WorkCenterId = (await centers.GetAllAsync(cancellationToken)).First().Id,
            Start = new DateTime(2026, 7, 1),
            End = new DateTime(2026, 7, 2)
        }, cancellationToken)).IsSuccess);
    }

    // ----- the pages -----

    private BrowserDatabase ArrangeWorkCenters(TempDatabaseFiles files, WorkspaceRole role)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("pages.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(role);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkCenters>>(NullLogger<WorkPlanStudio.Pages.WorkCenters>.Instance);
        return database;
    }

    [Fact]
    public async Task A_guest_sees_the_work_centers_but_no_way_to_change_them()
    {
        using var files = new TempDatabaseFiles();
        Assert.True((await ArrangeWorkCenters(files, WorkspaceRole.Guest).EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        Assert.DoesNotContain("WorkCenters_New", cut.Markup);
        Assert.Empty(cut.FindAll(".icon-btn"));
        Assert.Contains("ReadOnly_Notice", cut.Markup);
    }

    [Fact]
    public async Task A_planner_sees_every_action_and_no_notice()
    {
        using var files = new TempDatabaseFiles();
        Assert.True((await ArrangeWorkCenters(files, WorkspaceRole.Planner).EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        Assert.Contains("WorkCenters_New", cut.Markup);
        Assert.NotEmpty(cut.FindAll(".icon-btn"));
        Assert.DoesNotContain("ReadOnly_Notice", cut.Markup);
    }

    [Fact]
    public async Task Switching_persona_re_renders_the_page_without_a_reload()
    {
        using var files = new TempDatabaseFiles();
        Assert.True((await ArrangeWorkCenters(files, WorkspaceRole.Planner).EnsureReadyAsync()).IsReady);
        var provider = Services.GetRequiredService<DemoAuthenticationStateProvider>();

        var cut = Render<WorkPlanStudio.Pages.WorkCenters>();
        cut.WaitForAssertion(() => Assert.Contains("WorkCenters_New", cut.Markup));

        await cut.InvokeAsync(() => provider.SwitchAsync(WorkspaceRole.Guest, Xunit.TestContext.Current.CancellationToken));

        cut.WaitForAssertion(() => Assert.DoesNotContain("WorkCenters_New", cut.Markup));
        Assert.Contains("ReadOnly_Notice", cut.Markup);
    }

    [Fact]
    public async Task The_persona_menu_lists_every_persona_and_switches()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        var provider = Services.AddDemoAuthorization(WorkspaceRole.Planner);

        var cut = Render<PersonaMenu>();

        var options = cut.FindAll("[role=menuitemradio]");
        Assert.Equal(3, options.Count);
        Assert.Equal("true", options[0].GetAttribute("aria-checked"));   // Planner first and current

        await cut.InvokeAsync(() => options[2].Click());

        Assert.Equal(WorkspaceRole.Guest, provider.CurrentRole);
        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll("[role=menuitemradio]")[2].GetAttribute("aria-checked")));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "workplanMenu.close");
    }
}
