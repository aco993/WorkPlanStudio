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
/// Measured on the published 0.3.9: withdrawing a production order moved the row
/// to <em>Cancelled</em>, bumped the stored revision, moved focus to the page
/// heading — and the page held no live region at all, so a screen reader said
/// "Production Orders, heading": word for word what it says when the dialog is
/// merely dismissed. The application had learned in 0.3.8 to announce an empty
/// list; it never announced a deleted row.
/// </summary>
public sealed class WhatJustHappenedIsSaidOutLoudTests : AppBunitContext
{
    private readonly TempDatabaseFiles _files = new();

    private BrowserDatabase Arrange(WorkspaceRole role = WorkspaceRole.Planner)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = _files.CreateDatabase("announce.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(role);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.CostCenters>>(NullLogger<WorkPlanStudio.Pages.CostCenters>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkCenters>>(NullLogger<WorkPlanStudio.Pages.WorkCenters>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlans>>(NullLogger<WorkPlanStudio.Pages.WorkPlans>.Instance);
        return database;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _files.Dispose();
    }

    private async Task<BrowserDatabase> ReadyAsync(WorkspaceRole role = WorkspaceRole.Planner)
    {
        var database = Arrange(role);
        Assert.True((await database.EnsureReadyAsync()).IsReady);
        return database;
    }

    /// <summary>What the shipped resource file says for a key, so a test can read the real sentence.</summary>
    private static string Sentence(string key)
    {
        var resource = File.ReadAllText(Path.Join(
            RepoFiles.Root, "src", "WorkPlanStudio", "Resources", "SharedResource.resx"));
        var match = System.Text.RegularExpressions.Regex.Match(
            resource, $"<data name=\"{key}\"[^>]*><value>(?<v>.*?)</value>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, $"{key} is not in the resource file");
        return match.Groups["v"].Value;
    }

    /// <summary>Opens the confirm dialog behind <paramref name="trigger"/> and presses its danger button.</summary>
    private static async Task ConfirmAsync<TPage>(IRenderedComponent<TPage> cut, string trigger)
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        await cut.ActAsync(trigger, button => button.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        await cut.ActAsync(".modal-foot .btn-danger", confirm => confirm.Click());
    }

    [Fact]
    public async Task Cancelling_an_order_says_which_order_and_what_it_means()
    {
        await ReadyAsync();

        var cut = Render<WorkPlanStudio.Pages.ProductionOrders>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        await ConfirmAsync(cut, "tbody tr .btn-ghost");

        cut.WaitForAssertion(() => Assert.NotEmpty(Announcements));
        Assert.Contains(Announcements, said => said.Contains("Ann_OrderCancelled", StringComparison.Ordinal));

        // The test localiser returns the key, so which order it named is asserted
        // where the sentence lives: it has to take the number as an argument.
        Assert.Contains("{0}", Sentence("Ann_OrderCancelled"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_work_plan_says_which_one()
    {
        await ReadyAsync();

        var cut = Render<WorkPlanStudio.Pages.WorkPlans>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr .icon-btn.danger")));

        await ConfirmAsync(cut, "tbody tr .icon-btn.danger");

        cut.WaitForAssertion(() => Assert.NotEmpty(Announcements));
        Assert.Contains(Announcements, said => said.Contains("Ann_Deleted", StringComparison.Ordinal));
        Assert.Contains("{0}", Sentence("Ann_Deleted"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_cost_centre_is_not_the_same_sentence_as_updating_one()
    {
        await ReadyAsync();

        var cut = Render<WorkPlanStudio.Pages.CostCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        await cut.ActAsync(".page-head .btn-primary", add => add.Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".modal-card")));
        await cut.ActAsync("#cc-code", code => code.Input("CC-7777"));
        await cut.ActAsync("#cc-name", name => name.Input("Announcer probe"));
        await cut.ActAsync(".modal-foot .btn-primary", save => save.Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(Announcements));
        Assert.Contains(Announcements, said => said.Contains("Ann_Created", StringComparison.Ordinal));
        Assert.DoesNotContain(Announcements, said => said.Contains("Ann_Saved", StringComparison.Ordinal));
    }

    /// <summary>A refusal is already shown on screen; announcing "deleted" as well would be a lie.</summary>
    [Fact]
    public async Task A_refused_delete_announces_nothing()
    {
        await ReadyAsync(WorkspaceRole.Supervisor);

        var cut = Render<WorkPlanStudio.Pages.CostCenters>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        // A supervisor is offered no delete at all, which is the point: nothing
        // happened, so nothing is said.
        Assert.Empty(cut.FindAll("tbody tr .icon-btn.danger"));
        Assert.Empty(Announcements);
    }

    [Fact]
    public async Task The_region_alternates_so_the_same_sentence_twice_is_still_heard()
    {
        Arrange();
        var announcer = Services.GetRequiredService<WorkPlanStudio.Services.UiAnnouncer>();

        var cut = Render<WorkPlanStudio.Components.LiveAnnouncer>();
        var regions = cut.FindAll("[role=status]");
        Assert.Equal(2, regions.Count);
        Assert.All(regions, region => Assert.Equal("polite", region.GetAttribute("aria-live")));

        await cut.InvokeAsync(() => announcer.Say("same"));
        var first = cut.FindAll("[role=status]").Select(r => r.TextContent).ToList();

        await cut.InvokeAsync(() => announcer.Say("same"));
        var second = cut.FindAll("[role=status]").Select(r => r.TextContent).ToList();

        // Identical text in the same element is silent; the sentence has to move.
        Assert.NotEqual(first, second);
        Assert.Contains("same", string.Concat(second), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_announcement_is_not_announced()
    {
        Arrange();
        var announcer = Services.GetRequiredService<WorkPlanStudio.Services.UiAnnouncer>();

        var cut = Render<WorkPlanStudio.Components.LiveAnnouncer>();
        await cut.InvokeAsync(() => announcer.Say("   "));

        Assert.All(cut.FindAll("[role=status]"), region => Assert.Equal("", region.TextContent));
    }
}
