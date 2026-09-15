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
/// Measured on the published site by typing something that occurs nowhere: three
/// of the four lists showed a table header, zero rows and not one word. A screen
/// reader read the column titles and then stopped. The fourth said something — and
/// said the same thing whether or not anything had been searched for.
/// </summary>
public sealed class ListsSayWhenNothingMatchedTests : AppBunitContext
{
    private BrowserDatabase Arrange(TempDatabaseFiles files)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var database = files.CreateDatabase("nomatch.db", new FakeStorage());
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(database);
        Services.AddDemoAuthorization(WorkspaceRole.Planner);
        Services.AddSingleton(sp => new WorkCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new CostCenterService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new WorkPlanService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton(sp => new ProductionOrderService(database, sp.GetRequiredService<IPermissionGuard>()));
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.ProductionOrders>>(NullLogger<WorkPlanStudio.Pages.ProductionOrders>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkCenters>>(NullLogger<WorkPlanStudio.Pages.WorkCenters>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.CostCenters>>(NullLogger<WorkPlanStudio.Pages.CostCenters>.Instance);
        Services.AddSingleton<ILogger<WorkPlanStudio.Pages.WorkPlans>>(NullLogger<WorkPlanStudio.Pages.WorkPlans>.Instance);
        return database;
    }

    /// <summary>Renders a list, waits for its rows, then searches for something that is not there.</summary>
    private async Task<IRenderedComponent<TPage>> SearchedToNothingAsync<TPage>(TempDatabaseFiles files)
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<TPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));

        await cut.ActAsync("input[type=search]", search => search.Input("ZZZ-nothing-matches-this"));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("tbody tr")));
        return cut;
    }

    /// <summary>The sentence is announced, and the search that produced it is still reachable.</summary>
    private static void AssertSaysSoAndKeepsTheSearch<TPage>(IRenderedComponent<TPage> cut)
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        Assert.NotEmpty(cut.FindAll(".empty-state[role=status]"));
        Assert.Contains("List_NoMatches", cut.Markup, StringComparison.Ordinal);

        // Hiding the toolbar with the rows would leave the reader looking at a
        // sentence with no way back to the list it is about.
        Assert.NotEmpty(cut.FindAll("input[type=search]"));
    }

    [Fact]
    public async Task The_order_list_says_when_nothing_matched()
    {
        using var files = new TempDatabaseFiles();
        AssertSaysSoAndKeepsTheSearch(await SearchedToNothingAsync<WorkPlanStudio.Pages.ProductionOrders>(files));
    }

    [Fact]
    public async Task The_work_center_list_says_when_nothing_matched()
    {
        using var files = new TempDatabaseFiles();
        AssertSaysSoAndKeepsTheSearch(await SearchedToNothingAsync<WorkPlanStudio.Pages.WorkCenters>(files));
    }

    [Fact]
    public async Task The_cost_center_list_says_when_nothing_matched()
    {
        using var files = new TempDatabaseFiles();
        AssertSaysSoAndKeepsTheSearch(await SearchedToNothingAsync<WorkPlanStudio.Pages.CostCenters>(files));
    }

    [Fact]
    public async Task The_work_plan_list_says_it_too_and_still_announces_it()
    {
        // This one already said something; it did not say it as a status, so a
        // reader typing in the search box was not told the result had changed.
        using var files = new TempDatabaseFiles();
        AssertSaysSoAndKeepsTheSearch(await SearchedToNothingAsync<WorkPlanStudio.Pages.WorkPlans>(files));
    }

    [Fact]
    public async Task A_list_with_rows_shows_them_and_no_such_message()
    {
        // A message that is always there says nothing.
        using var files = new TempDatabaseFiles();
        var database = Arrange(files);
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var cut = Render<WorkPlanStudio.Pages.CostCenters>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("tbody tr")));
        Assert.DoesNotContain("List_NoMatches", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_database_and_an_empty_search_are_different_sentences()
    {
        // The work-plan list used one string for both, and it read as the second:
        // "No work plans match your search" over a database that had none in it.
        foreach (var file in new[] { "SharedResource.resx", "SharedResource.de.resx" })
        {
            var resource = File.ReadAllText(Path.Join(
                RepoFiles.Root, "src", "WorkPlanStudio", "Resources", file));

            Assert.DoesNotContain("such", Value(resource, "WorkPlans_Empty"), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("search", Value(resource, "WorkPlans_Empty"), StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(Value(resource, "List_NoMatches"));
        }
    }

    private static string Value(string resx, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            resx, $"<data name=\"{key}\"[^>]*><value>(?<v>.*?)</value>",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, $"{key} is not in the resource file");
        return match.Groups["v"].Value;
    }
}
